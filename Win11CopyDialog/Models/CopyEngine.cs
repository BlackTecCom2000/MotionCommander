using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Win11CopyDialog.Modules.PerformanceEngine;

namespace Win11CopyDialog.Models;

/// <summary>
/// Универсальный движок операции копирования.
/// Режим Simulation — правдоподобная симуляция скорости (для демо и предпросмотра).
/// Режим Real — побайтовое копирование файлов с поддержкой паузы/отмены.
/// </summary>
public sealed class CopyEngine : INotifyPropertyChanged, IDisposable
{
    private readonly DispatcherTimer _tick;
    private readonly Random _rnd = new();
    private readonly ManualResetEventSlim _pauseGate = new(true);
    private CancellationTokenSource? _realCts;
    private DateTime _startedAt;
    private TimeSpan _pausedTotal = TimeSpan.Zero;
    private DateTime? _pausedSince;
    private double _smoothedSpeed;
    private double _wavePhase;

    public ObservableCollection<CopyItem> Items { get; } = new();
    public List<double> SpeedHistory { get; } = new(); // байт/с, последние 90 сэмплов
    public const int MaxHistory = 90;

    public long TotalBytes { get; private set; }
    public bool IsRealMode { get; private set; }

    /// <summary>Базовая скорость симуляции, байт/с. Меняется слайдером.</summary>
    public double BaseSpeedBytesPerSec { get; set; } = 150 * 1024 * 1024;

    private long _copiedBytes;
    public long CopiedBytes
    {
        get => _copiedBytes;
        private set { _copiedBytes = value; OnChanged(); OnChanged(nameof(OverallProgress)); OnChanged(nameof(RemainingBytes)); }
    }

    private double _currentSpeed;
    public double CurrentSpeed
    {
        get => _currentSpeed;
        private set { _currentSpeed = value; OnChanged(); }
    }

    private bool _isPaused;
    public bool IsPaused
    {
        get => _isPaused;
        private set { _isPaused = value; OnChanged(); OnChanged(nameof(StateText)); }
    }

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set { _isRunning = value; OnChanged(); OnChanged(nameof(StateText)); }
    }

    private bool _isCompleted;
    public bool IsCompleted
    {
        get => _isCompleted;
        private set { _isCompleted = value; OnChanged(); OnChanged(nameof(StateText)); }
    }

    private bool _isCancelled;
    public bool IsCancelled
    {
        get => _isCancelled;
        private set { _isCancelled = value; OnChanged(); OnChanged(nameof(StateText)); }
    }

    public double OverallProgress => TotalBytes <= 0 ? 0 : CopiedBytes * 100.0 / TotalBytes;
    public long RemainingBytes => Math.Max(0, TotalBytes - CopiedBytes);
    public TimeSpan Elapsed => (IsPaused && _pausedSince.HasValue ? _pausedSince.Value : DateTime.Now) - _startedAt - _pausedTotal;
    public TimeSpan Eta => CurrentSpeed > 1 ? TimeSpan.FromSeconds(RemainingBytes / CurrentSpeed) : TimeSpan.Zero;

    public int DoneCount => Items.Count(i => i.IsFinished);
    public CopyItem? CurrentItem => Items.FirstOrDefault(i => i.Status == CopyItemStatus.Copying)
                                    ?? Items.FirstOrDefault(i => !i.IsFinished);

    public string StateText => IsCancelled ? "Отменено" : IsCompleted ? "Завершено" : IsPaused ? "Приостановлено" : IsRunning ? "Копирование…" : "Готово";

    public event EventHandler? Completed;
    public event EventHandler? ProgressTick;

    public CopyEngine()
    {
        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _tick.Tick += (_, _) => SimulationTick();
    }

    // ---------- Simulation ----------

    public void LoadSimulation(IEnumerable<(string name, long size)> files, double? speedBytesPerSec = null)
    {
        Reset();
        IsRealMode = false;
        if (speedBytesPerSec.HasValue) BaseSpeedBytesPerSec = speedBytesPerSec.Value;
        foreach (var (name, size) in files)
            Items.Add(new CopyItem(name, size));
        TotalBytes = Items.Sum(i => i.SizeBytes);
        OnChanged(nameof(TotalBytes));
        _startedAt = DateTime.Now;
        IsRunning = true;
        _tick.Start();
    }

    private void SimulationTick()
    {
        if (IsPaused || IsCompleted || IsCancelled || IsRealMode) return;

        // Правдоподобный профиль скорости: синусоида + шум + редкие просадки (кэш/диск)
        _wavePhase += 0.12;
        double wave = 1 + 0.22 * Math.Sin(_wavePhase) + 0.08 * Math.Sin(_wavePhase * 2.7);
        double noise = 0.9 + _rnd.NextDouble() * 0.2;
        double dip = _rnd.NextDouble() < 0.03 ? 0.35 + _rnd.NextDouble() * 0.3 : 1.0;
        double instant = BaseSpeedBytesPerSec * wave * noise * dip;

        _smoothedSpeed = _smoothedSpeed <= 0 ? instant : _smoothedSpeed * 0.7 + instant * 0.3;
        CurrentSpeed = _smoothedSpeed;
        PushHistory(_smoothedSpeed);

        long chunk = (long)(instant * 0.1); // 100 мс
        Advance(chunk);
        OnChanged(nameof(Elapsed));
        OnChanged(nameof(Eta));
        ProgressTick?.Invoke(this, EventArgs.Empty);
    }

    private void Advance(long bytes)
    {
        long left = bytes;
        foreach (var item in Items)
        {
            if (left <= 0) break;
            if (item.IsFinished) continue;
            if (item.Status != CopyItemStatus.Copying) item.Status = CopyItemStatus.Copying;

            long need = item.SizeBytes - item.CopiedBytes;
            long take = Math.Min(need, left);
            item.CopiedBytes += take;
            CopiedBytes += take;
            left -= take;

            if (item.CopiedBytes >= item.SizeBytes)
                item.Status = CopyItemStatus.Done;
        }

        if (CopiedBytes >= TotalBytes)
            Finish(completed: true);
    }

    // ---------- Real copy ----------

    public static List<(string sourceFile, string destFile)> ExpandSourcesToFiles(IEnumerable<(string source, string dest)> inputs)
    {
        var result = new List<(string sourceFile, string destFile)>();
        foreach (var (src, dst) in inputs)
        {
            if (string.IsNullOrWhiteSpace(src)) continue;

            if (Directory.Exists(src))
            {
                // Рекурсивное сканирование папки
                string folderName = Path.GetFileName(src.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(folderName)) folderName = "Folder";

                string targetBaseDir = dst;
                if (Directory.Exists(dst))
                {
                    string dstName = Path.GetFileName(dst.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    if (!string.Equals(dstName, folderName, StringComparison.OrdinalIgnoreCase))
                    {
                        targetBaseDir = Path.Combine(dst, folderName);
                    }
                }

                try { Directory.CreateDirectory(targetBaseDir); } catch { }

                try
                {
                    foreach (var sub in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
                    {
                        string rel = Path.GetRelativePath(src, sub);
                        try { Directory.CreateDirectory(Path.Combine(targetBaseDir, rel)); } catch { }
                    }
                }
                catch { }

                try
                {
                    foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
                    {
                        string rel = Path.GetRelativePath(src, file);
                        result.Add((file, Path.Combine(targetBaseDir, rel)));
                    }
                }
                catch { }
            }
            else if (File.Exists(src))
            {
                string targetFile = dst;
                if (Directory.Exists(dst))
                {
                    targetFile = Path.Combine(dst, Path.GetFileName(src));
                }
                else
                {
                    string? pDir = Path.GetDirectoryName(dst);
                    if (!string.IsNullOrEmpty(pDir))
                    {
                        try { Directory.CreateDirectory(pDir); } catch { }
                    }
                }

                if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(targetFile), StringComparison.OrdinalIgnoreCase))
                {
                    targetFile = GenerateDuplicateFileName(targetFile);
                }

                result.Add((src, targetFile));
            }
        }
        return result;
    }

    private static string GenerateDuplicateFileName(string filePath)
    {
        string dir = Path.GetDirectoryName(filePath) ?? "";
        string name = Path.GetFileNameWithoutExtension(filePath);
        string ext = Path.GetExtension(filePath);
        int counter = 1;
        string candidate;
        do
        {
            candidate = Path.Combine(dir, $"{name} - Копия{(counter > 1 ? $" ({counter})" : "")}{ext}");
            counter++;
        } while (File.Exists(candidate));
        return candidate;
    }

    public async Task StartRealCopyAsync(IEnumerable<(string source, string dest)> files, CancellationToken outer = default)
    {
        Reset();
        IsRealMode = true;
        _realCts = CancellationTokenSource.CreateLinkedTokenSource(outer);

        var filePairs = ExpandSourcesToFiles(files);
        foreach (var (s, d) in filePairs)
        {
            long size = 0;
            try { size = new FileInfo(s).Length; } catch { }
            Items.Add(new CopyItem(Path.GetFileName(s), size, s, d));
        }

        TotalBytes = Items.Sum(i => i.SizeBytes);
        OnChanged(nameof(TotalBytes));
        _startedAt = DateTime.Now;
        IsRunning = true;

        if (Items.Count == 0)
        {
            Finish(completed: true);
            return;
        }

        _tick.Start();

        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            foreach (var item in Items)
            {
                _realCts.Token.ThrowIfCancellationRequested();
                if (item.Status == CopyItemStatus.Skipped) continue;
                item.Status = CopyItemStatus.Copying;
                OnChanged(nameof(CurrentItem));

                await CopyOneFileAsync(item, _realCts.Token);
                item.Status = CopyItemStatus.Done;

                double sec = Math.Max(0.1, sw.Elapsed.TotalSeconds);
                _smoothedSpeed = CopiedBytes / sec;
                CurrentSpeed = _smoothedSpeed;
                PushHistory(_smoothedSpeed);
                OnChanged(nameof(Elapsed));
                OnChanged(nameof(Eta));
                ProgressTick?.Invoke(this, EventArgs.Empty);
            }
            Finish(completed: true);
        }
        catch (OperationCanceledException)
        {
            Finish(completed: false, cancelled: true);
        }
        catch (Exception)
        {
            var cur = CurrentItem;
            if (cur != null) cur.Status = CopyItemStatus.Error;
            Finish(completed: false);
        }
    }

    private async Task CopyOneFileAsync(CopyItem item, CancellationToken ct)
    {
        var scenario = HardwareAnalyzer.AnalyzeTransferScenario(item.SourcePath, item.DestPath);
        int buf = scenario.RecommendedBufferSize;
        long lastBytes = 0;

        await StreamingPipeline.CopyStreamPipelineAsync(
            item.SourcePath,
            item.DestPath,
            buf,
            telemetry =>
            {
                long delta = telemetry.BytesTransferred - lastBytes;
                if (delta > 0)
                {
                    lastBytes = telemetry.BytesTransferred;
                    CopiedBytes += delta;
                }
                item.CopiedBytes = telemetry.BytesTransferred;
                if (telemetry.InstantThroughputBytesPerSec > 0)
                {
                    CurrentSpeed = telemetry.InstantThroughputBytesPerSec;
                    PushHistory(CurrentSpeed);
                }
                OnChanged(nameof(CopiedBytes));
                OnChanged(nameof(RemainingBytes));
                OnChanged(nameof(OverallProgress));
                OnChanged(nameof(Elapsed));
                OnChanged(nameof(Eta));
                ProgressTick?.Invoke(this, EventArgs.Empty);
            },
            _pauseGate,
            ct);

        item.CopiedBytes = item.SizeBytes;
    }

    // ---------- Управление ----------

    public void Pause()
    {
        if (!IsRunning || IsCompleted || IsCancelled || IsPaused) return;
        IsPaused = true;
        _pausedSince = DateTime.Now;
        _pauseGate.Reset();
        var cur = Items.FirstOrDefault(i => i.Status == CopyItemStatus.Copying);
        if (cur != null) cur.Status = CopyItemStatus.Paused;
        CurrentSpeed = 0;
        PushHistory(0);
    }

    public void Resume()
    {
        if (!IsPaused) return;
        if (_pausedSince.HasValue) _pausedTotal += DateTime.Now - _pausedSince.Value;
        _pausedSince = null;
        IsPaused = false;
        _pauseGate.Set();
        var cur = Items.FirstOrDefault(i => i.Status == CopyItemStatus.Paused);
        if (cur != null) cur.Status = CopyItemStatus.Copying;
    }

    public void Cancel()
    {
        if (IsCompleted || IsCancelled) return;
        _tick.Stop();
        _realCts?.Cancel();
        _pauseGate.Set();
        IsCancelled = true;
        IsRunning = false;
    }

    public void SkipCurrent()
    {
        var cur = CurrentItem;
        if (cur == null || cur.IsFinished) return;
        CopiedBytes += cur.RemainingBytes; // пропущенное засчитываем как обработанное
        cur.CopiedBytes = cur.SizeBytes;
        cur.Status = CopyItemStatus.Skipped;
        if (CopiedBytes >= TotalBytes) Finish(completed: true);
    }

    private void Finish(bool completed, bool cancelled = false)
    {
        _tick.Stop();
        IsRunning = false;
        IsCompleted = completed;
        IsCancelled = cancelled;
        if (completed) { CurrentSpeed = 0; }
        Completed?.Invoke(this, EventArgs.Empty);
        OnChanged(nameof(Elapsed));
        OnChanged(nameof(Eta));
    }

    private void PushHistory(double v)
    {
        SpeedHistory.Add(v);
        if (SpeedHistory.Count > MaxHistory) SpeedHistory.RemoveAt(0);
    }

    private void Reset()
    {
        _tick.Stop();
        _realCts?.Cancel();
        _realCts = null;
        Items.Clear();
        SpeedHistory.Clear();
        TotalBytes = 0;
        CopiedBytes = 0;
        CurrentSpeed = 0;
        _smoothedSpeed = 0;
        _wavePhase = 0;
        _pausedTotal = TimeSpan.Zero;
        _pausedSince = null;
        IsPaused = false;
        IsRunning = false;
        IsCompleted = false;
        IsCancelled = false;
        _pauseGate.Set();
    }

    public void Dispose()
    {
        _tick.Stop();
        _realCts?.Cancel();
        _pauseGate.Dispose();
        _realCts?.Dispose();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
