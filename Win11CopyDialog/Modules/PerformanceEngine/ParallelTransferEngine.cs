using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using Win11CopyDialog.Helpers;

namespace Win11CopyDialog.Modules.PerformanceEngine;

public sealed class TransferTaskItem
{
    public string SourcePath { get; }
    public string DestPath { get; }
    public long SizeBytes { get; }
    public bool IsDirectory { get; }

    public TransferTaskItem(string sourcePath, string destPath, long sizeBytes, bool isDirectory = false)
    {
        SourcePath = sourcePath;
        DestPath = destPath;
        SizeBytes = sizeBytes;
        IsDirectory = isDirectory;
    }
}

public sealed class LiveTransferTelemetry
{
    public long TotalBytes { get; set; }
    public long CopiedBytes { get; set; }
    public double ProgressPercent => TotalBytes > 0 ? (double)CopiedBytes / TotalBytes * 100.0 : 0;
    public double CurrentThroughputBytesPerSec { get; set; }
    public double AverageThroughputBytesPerSec { get; set; }
    public double PeakThroughputBytesPerSec { get; set; }
    public double ReadLatencyMs { get; set; }
    public double WriteLatencyMs { get; set; }
    public int ActiveWorkers { get; set; }
    public int BufferSizeBytes { get; set; }
    public int FilesRemaining { get; set; }
    public int TotalFiles { get; set; }
    public TimeSpan Elapsed { get; set; }
    public TimeSpan EstimatedRemaining { get; set; }
    public BottleneckAnalysisResult? Bottleneck { get; set; }
    public TransferScenarioProfile? Scenario { get; set; }
    public string CurrentFileName { get; set; } = "";
}

/// <summary>
/// Адаптивное ядро передачи данных нового поколения (ParallelTransferEngine):
/// реализует аппаратную адаптацию под тип накопителя (NVMe/SSD/HDD),
/// раздельные конвейеры для крупных и мелких файлов, двухбуферный потоковый стриминг,
/// пул буферов без аллокаций в куче и полностью изолированную от I/O телеметрию.
/// </summary>
public sealed class ParallelTransferEngine : IDisposable
{
    private readonly ManualResetEventSlim _pauseGate = new(true);
    private CancellationTokenSource? _cts;
    private readonly Stopwatch _stopwatch = new();

    private long _totalBytes;
    private long _copiedBytes;
    private long _filesCompleted;
    private int _totalFiles;

    private double _peakSpeed;
    private double _currentSpeed;
    private double _latestReadLatency;
    private double _latestWriteLatency;

    private int _activeWorkers;
    private string _currentFile = "";

    public bool IsRunning { get; private set; }
    public bool IsPaused { get; private set; }
    public bool IsCompleted { get; private set; }
    public bool IsCancelled { get; private set; }

    public event Action<LiveTransferTelemetry>? TelemetryTick;
    public event Action? Completed;

    public void Pause()
    {
        if (!IsRunning || IsPaused) return;
        IsPaused = true;
        _pauseGate.Reset();
    }

    public void Resume()
    {
        if (!IsPaused) return;
        IsPaused = false;
        _pauseGate.Set();
    }

    public void Cancel()
    {
        _cts?.Cancel();
        _pauseGate.Set();
        IsCancelled = true;
        IsRunning = false;
    }

    /// <summary>
    /// Запуск высокопроизводительной передачи списка файлов или папок.
    /// </summary>
    public async Task StartTransferAsync(IReadOnlyList<(string src, string dst)> items, CancellationToken externalCt = default)
    {
        if (items.Count == 0) return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        var ct = _cts.Token;

        IsRunning = true;
        IsPaused = false;
        IsCompleted = false;
        IsCancelled = false;
        _pauseGate.Set();

        _copiedBytes = 0;
        _filesCompleted = 0;
        _peakSpeed = 0;
        _currentSpeed = 0;
        _stopwatch.Restart();

        // 1. Аппаратный анализ сценария
        var firstSrc = items[0].src;
        var firstDst = items[0].dst;
        var scenario = HardwareAnalyzer.AnalyzeTransferScenario(firstSrc, firstDst);

        // 2. Сканирование и группировка задач
        var taskList = new List<TransferTaskItem>();
        long totalBytesCalc = 0;

        foreach (var (src, dst) in items)
        {
            if (File.Exists(src))
            {
                var fi = new FileInfo(src);
                taskList.Add(new TransferTaskItem(src, dst, fi.Length));
                totalBytesCalc += fi.Length;
            }
            else if (Directory.Exists(src))
            {
                var srcDir = new DirectoryInfo(src);
                string baseParent = srcDir.Parent != null ? srcDir.Parent.FullName : srcDir.FullName;

                // Пакетная предгенерация каталогов
                foreach (var dir in srcDir.EnumerateDirectories("*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(baseParent, dir.FullName);
                    string targetSubDir = Path.Combine(dst, rel);
                    Directory.CreateDirectory(targetSubDir);
                }

                foreach (var file in srcDir.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(baseParent, file.FullName);
                    string targetFilePath = Path.Combine(dst, rel);
                    taskList.Add(new TransferTaskItem(file.FullName, targetFilePath, file.Length));
                    totalBytesCalc += file.Length;
                }
            }
        }

        _totalBytes = totalBytesCalc;
        _totalFiles = taskList.Count;

        // Разделение на крупные (>16 МБ) и мелкие (<=16 МБ) файлы
        const long LargeThreshold = 16 * 1024 * 1024;
        var largeFiles = taskList.Where(t => t.SizeBytes >= LargeThreshold).ToList();
        var smallFiles = taskList.Where(t => t.SizeBytes < LargeThreshold).ToList();

        // Запуск фонового таймера опроса телеметрии (развязка с критическим I/O путем)
        using var timerCts = new CancellationTokenSource();
        var telemetryTask = StartTelemetryLoopAsync(scenario, smallFiles.Count > 0 && largeFiles.Count == 0, timerCts.Token);

        try
        {
            // 3. Выполнение передачи крупных файлов (потоковый двухбуферный конвейер)
            foreach (var item in largeFiles)
            {
                ct.ThrowIfCancellationRequested();
                _currentFile = Path.GetFileName(item.SourcePath);
                Interlocked.Exchange(ref _activeWorkers, 1);

                await StreamingPipeline.CopyStreamPipelineAsync(
                    item.SourcePath,
                    item.DestPath,
                    scenario.RecommendedBufferSize,
                    telemetry =>
                    {
                        _latestReadLatency = telemetry.ReadLatencyMs;
                        _latestWriteLatency = telemetry.WriteLatencyMs;
                    },
                    _pauseGate,
                    ct);

                Interlocked.Add(ref _copiedBytes, item.SizeBytes);
                Interlocked.Increment(ref _filesCompleted);
            }

            // 4. Выполнение передачи мелких файлов (адаптивный параллельный пул)
            if (smallFiles.Count > 0)
            {
                int concurrency = scenario.RecommendedConcurrency;
                Interlocked.Exchange(ref _activeWorkers, concurrency);

                var queue = new ConcurrentQueue<TransferTaskItem>(smallFiles);
                var workers = new List<Task>();

                for (int w = 0; w < concurrency; w++)
                {
                    workers.Add(Task.Run(async () =>
                    {
                        const int smallBuf = 256 * 1024;
                        while (queue.TryDequeue(out var taskItem))
                        {
                            if (ct.IsCancellationRequested) break;
                            if (!_pauseGate.IsSet) _pauseGate.Wait(ct);

                            _currentFile = Path.GetFileName(taskItem.SourcePath);
                            await CopySmallFileDirectAsync(taskItem, smallBuf, ct);

                            Interlocked.Add(ref _copiedBytes, taskItem.SizeBytes);
                            Interlocked.Increment(ref _filesCompleted);
                        }
                    }, ct));
                }

                await Task.WhenAll(workers);
            }

            IsCompleted = true;
        }
        catch (OperationCanceledException)
        {
            IsCancelled = true;
        }
        finally
        {
            timerCts.Cancel();
            try { await telemetryTask; } catch { }
            IsRunning = false;
            _stopwatch.Stop();
            Completed?.Invoke();
        }
    }

    private async Task CopySmallFileDirectAsync(TransferTaskItem item, int bufSize, CancellationToken ct)
    {
        string? destDir = Path.GetDirectoryName(item.DestPath);
        if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
            Directory.CreateDirectory(destDir);

        using var srcStream = new FileStream(item.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufSize, useAsync: true);
        using var dstStream = new FileStream(item.DestPath, FileMode.Create, FileAccess.Write, FileShare.None, bufSize, useAsync: true);

        using var pBuf = BufferPool.Rent(bufSize);
        int read;
        while ((read = await srcStream.ReadAsync(pBuf.Memory, ct)) > 0)
        {
            await dstStream.WriteAsync(pBuf.Memory.Slice(0, read), ct);
        }

        try
        {
            File.SetLastWriteTime(item.DestPath, File.GetLastWriteTime(item.SourcePath));
        }
        catch { }
    }

    /// <summary>
    /// Изолированный опрос телеметрии с частотой 30 Гц без взаимных блокировок.
    /// </summary>
    private async Task StartTelemetryLoopAsync(TransferScenarioProfile scenario, bool isSmallFiles, CancellationToken ct)
    {
        long lastBytes = 0;
        long lastTimestamp = Stopwatch.GetTimestamp();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(33, ct); // ~30 кадров в секунду
            }
            catch (OperationCanceledException)
            {
                break;
            }

            long currentBytes = Interlocked.Read(ref _copiedBytes);
            long now = Stopwatch.GetTimestamp();
            double intervalSec = (now - lastTimestamp) / (double)Stopwatch.Frequency;

            if (intervalSec > 0.05)
            {
                double speed = (currentBytes - lastBytes) / intervalSec;
                if (!IsPaused && speed > 0)
                {
                    _currentSpeed = speed;
                    if (speed > _peakSpeed) _peakSpeed = speed;
                }
                else if (IsPaused)
                {
                    _currentSpeed = 0;
                }

                lastBytes = currentBytes;
                lastTimestamp = now;
            }

            double elapsedSec = _stopwatch.Elapsed.TotalSeconds;
            double avgSpeed = elapsedSec > 0.1 ? currentBytes / elapsedSec : 0;
            long remBytes = Math.Max(0, _totalBytes - currentBytes);
            TimeSpan eta = _currentSpeed > 1024 ? TimeSpan.FromSeconds(remBytes / _currentSpeed) : TimeSpan.Zero;

            var sysInfo = SystemResourceMonitor.GetSnapshot();
            var bottleneck = BottleneckDetector.Analyze(
                _currentSpeed,
                _latestReadLatency,
                _latestWriteLatency,
                sysInfo.CpuTotalPercent,
                sysInfo.AvailableMemoryGb,
                scenario.SourceDisk?.MediaType ?? StorageMediaType.Unknown,
                scenario.DestinationDisk?.MediaType ?? StorageMediaType.Unknown,
                isSmallFiles);

            TelemetryTick?.Invoke(new LiveTransferTelemetry
            {
                TotalBytes = _totalBytes,
                CopiedBytes = currentBytes,
                CurrentThroughputBytesPerSec = _currentSpeed,
                AverageThroughputBytesPerSec = avgSpeed,
                PeakThroughputBytesPerSec = _peakSpeed,
                ReadLatencyMs = _latestReadLatency,
                WriteLatencyMs = _latestWriteLatency,
                ActiveWorkers = _activeWorkers,
                BufferSizeBytes = scenario.RecommendedBufferSize,
                FilesRemaining = Math.Max(0, _totalFiles - (int)Interlocked.Read(ref _filesCompleted)),
                TotalFiles = _totalFiles,
                Elapsed = _stopwatch.Elapsed,
                EstimatedRemaining = eta,
                Bottleneck = bottleneck,
                Scenario = scenario,
                CurrentFileName = _currentFile
            });
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _pauseGate.Dispose();
    }
}
