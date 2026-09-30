using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Win11CopyDialog.Modules.PerformanceEngine;
using Win11CopyDialog.Helpers;

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
    private CancellationTokenSource? _activeItemCts;
    private DateTime _startedAt;
    private TimeSpan _pausedTotal = TimeSpan.Zero;
    private DateTime? _pausedSince;
    private double _smoothedSpeed;
    private double _wavePhase;

    public ObservableCollection<CopyItem> Items { get; } = new();

    /// <summary>
    /// История скоростей, байт/с.
    ///
    /// <para>Раньше это была обычная List&lt;double&gt;, в которую писал
    /// поток конвейера (PushHistory), а UI-поток читал её в OnRender и
    /// RefreshUi: сначала считывали Count, затем индексировали. RemoveAt(0)
    /// сдвигает массив, поэтому между этими двумя действиями индекс мог
    /// выйти за границу — ArgumentOutOfRangeException прямо во время
    /// отрисовки, и повторялся каждый кадр.</para>
    ///
    /// <para>Теперь доступ синхронизирован, а снимок для отрисовки
    /// берётся атомарно в виде массива.</para>
    /// </summary>
    private readonly List<double> _speedHistory = new();
    private readonly object _historyLock = new();
    public const int MaxHistory = 90;

    /// <summary>Потокобезопасный снимок истории для отрисовки.</summary>
    public double[] SpeedHistorySnapshot()
    {
        lock (_historyLock) return _speedHistory.ToArray();
    }

    /// <summary>Максимум истории без копирования — для одиночных запросов.</summary>
    public double SpeedHistoryMax
    {
        get { lock (_historyLock) return _speedHistory.Count == 0 ? 0 : _speedHistory.Max(); }
    }

    public int SpeedHistoryCount
    {
        get { lock (_historyLock) return _speedHistory.Count; }
    }

    public double SpeedHistoryAt(int index)
    {
        lock (_historyLock) return _speedHistory[index];
    }

    public long TotalBytes { get; private set; }
    public bool IsRealMode { get; private set; }

    /// <summary>
    /// Что делать с уже существующим файлом назначения.
    /// По умолчанию — создать уникальное имя: это единственный режим,
    /// который не может привести к потере данных.
    /// </summary>
    public OverwritePolicy DestinationPolicy { get; set; } = OverwritePolicy.AutoRename;

    /// <summary>
    /// Текст последней ошибки операции. Показывается пользователю, потому
    /// что раньше сбой перечисления или копирования был полностью немым.
    /// </summary>
    public string OperationError { get; private set; } = "";

    /// <summary>Базовая скорость симуляции, байт/с. Меняется слайдером.</summary>
    public double BaseSpeedBytesPerSec { get; set; } = 150 * 1024 * 1024;

    private long _copiedBytes;
    public long CopiedBytes
    {
        get => Interlocked.Read(ref _copiedBytes);
        private set
        {
            Interlocked.Exchange(ref _copiedBytes, value);
            OnChanged();
            OnChanged(nameof(OverallProgress));
            OnChanged(nameof(RemainingBytes));
        }
    }

    /// <summary>
    /// Прибавление байт потокобезопасно.
    /// Раньше в телеметрии было `CopiedBytes += delta`, а в SkipCurrent —
    /// `CopiedBytes += cur.RemainingBytes` с UI-потока. Это неатомарное
    /// чтение-изменение-запись, из-за чего счётчик терял обновления и
    /// прогресс мог идти назад.
    /// </summary>
    private void AddCopiedBytes(long delta) => Interlocked.Add(ref _copiedBytes, delta);

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

    /// <summary>
    /// Оставлено для совместимости. Раньше здесь был единственный перебор
    /// источников, и он глотал ошибки перечисления.
    ///
    /// <para>Старая реализация удалена в CopySourceExpander, потому что
    /// невозможно было сообщить вызывающему коду, что папка НЕ была
    /// прочитана. Именно это приводило к удалению исходников: пустой
    /// список -> Finish(completed: true) -> Directory.Delete(source, true).</para>
    /// </summary>
    public static List<(string sourceFile, string destFile)> ExpandSourcesToFiles(IEnumerable<(string source, string dest)> inputs)
        => CopySourceExpander.Expand(inputs, OverwritePolicy.AutoRename).Pairs;



    public async Task StartRealCopyAsync(IEnumerable<(string source, string dest)> files, CancellationToken outer = default)
    {
        Reset();
        IsRealMode = true;
        _realCts = CancellationTokenSource.CreateLinkedTokenSource(outer);

        // Развёртка теперь сообщает об ошибках, а не молчит о них.
        // Раньше падение перечисления (например, один запрещённый подкаталог)
        // давало пустой список -> Finish(completed: true) -> вызывающий код
        // удалял исходную папку целиком.
        var expansion = CopySourceExpander.Expand(files, DestinationPolicy);

        if (expansion.HasFailures)
        {
            IsRealMode = true;
            foreach (var f in expansion.FailedSources)
                Items.Add(new CopyItem(Path.GetFileName(f), 0, f, "") { Status = CopyItemStatus.Error });

            OperationError = expansion.FailedSources.Count > 0
                ? "Не удалось прочитать источник: " + string.Join("; ", expansion.FailedSources.Take(5))
                  + (expansion.FailedSources.Count > 5 ? $" и ещё {expansion.FailedSources.Count - 5}" : "")
                  + ". Исходные файлы НЕ удалены."
                : expansion.EnumerationError + " Исходные файлы НЕ удалены.";

            if (expansion.SkippedReparsePoints > 0)
                OperationError += $" Пропущено каталогов-ссылок: {expansion.SkippedReparsePoints}.";

            OnChanged(nameof(OperationError));
            Finish(completed: false);
            return;
        }

        foreach (var (s, d) in expansion.Pairs)
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
            // Пустой список файлов — это не «успех». Раньше здесь стояло
            // Finish(completed: true), что приводило к удалению исходников.
            OperationError = "Не найдено ни одного файла для копирования.";
            OnChanged(nameof(OperationError));
            Finish(completed: false);
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

                using var itemCts = CancellationTokenSource.CreateLinkedTokenSource(_realCts.Token);
                _activeItemCts = itemCts;

                try
                {
                    await CopyOneFileAsync(item, itemCts.Token);
                    if (item.Status != CopyItemStatus.Skipped)
                    {
                        item.Status = CopyItemStatus.Done;
                    }
                }
                catch (OperationCanceledException) when (item.WasSkipped || item.Status == CopyItemStatus.Skipped)
                {
                    // Пользователь нажал «Пропустить» для этого файла
                    item.Status = CopyItemStatus.Skipped;
                    try { if (File.Exists(item.DestPath)) File.Delete(item.DestPath); } catch { }
                }
                catch (OperationCanceledException)
                {
                    // Отмена всей операции
                    throw;
                }
                catch (Exception ex)
                {
                    // Ошибка копирования одного файла не должна срывать всю очередь файлов
                    item.Status = CopyItemStatus.Error;
                    OperationError = $"Ошибка при копировании «{Path.GetFileName(item.SourcePath)}»: {ex.Message}";
                    OnChanged(nameof(OperationError));
                }
                finally
                {
                    _activeItemCts = null;
                }

                double sec = Math.Max(0.1, sw.Elapsed.TotalSeconds);
                _smoothedSpeed = CopiedBytes / sec;
                CurrentSpeed = _smoothedSpeed;
                PushHistory(_smoothedSpeed);
                OnChanged(nameof(Elapsed));
                OnChanged(nameof(Eta));
                ProgressTick?.Invoke(this, EventArgs.Empty);
            }

            // Отчёт об успехе только если действительно всё скопировано без фатальных ошибок.
            var notDone = Items.Where(i => i.Status != CopyItemStatus.Done && i.Status != CopyItemStatus.Skipped).ToList();
            if (notDone.Count > 0)
            {
                OperationError = $"Не все файлы скопированы: ошибок {notDone.Count}. Исходные файлы оставлены на месте.";
                OnChanged(nameof(OperationError));
                Finish(completed: false);
                return;
            }

            Finish(completed: true);
        }
        catch (OperationCanceledException)
        {
            // Отменённый файл остаётся в статусе Copying, из-за чего
            // CurrentItem навсегда указывал на него, а интерфейс показывал
            // «Копирование…» без прогресса. Помечаем явно.
            var cur = Items.FirstOrDefault(i => i.Status == CopyItemStatus.Copying);
            if (cur != null) cur.Status = CopyItemStatus.Error;

            OperationError = "Копирование отменено. Исходные файлы оставлены на месте.";
            OnChanged(nameof(OperationError));
            OnChanged(nameof(CurrentItem));
            Finish(completed: false, cancelled: true);
        }
        catch (Exception ex)
        {
            var cur = Items.FirstOrDefault(i => i.Status == CopyItemStatus.Copying);
            if (cur != null) cur.Status = CopyItemStatus.Error;

            OperationError = "Ошибка копирования: " + ex.Message + " Исходные файлы оставлены на месте.";
            OnChanged(nameof(OperationError));
            OnChanged(nameof(CurrentItem));
            Finish(completed: false);
        }
    }

    private async Task CopyOneFileAsync(CopyItem item, CancellationToken ct)
    {
        var scenario = HardwareAnalyzer.AnalyzeTransferScenario(item.SourcePath, item.DestPath);

        // Размер буфера берётся из НАСТРОЕК пользователя, а не только из
        // аппаратного сценария. Раньше поле DefaultBufferSizeKb записывалось
        // в settings.json и не читалось никем: движок всегда использовал
        // собственную рекомендацию, а флажок в настройках ничего не менял.
        int buf = IoSettings.BufferSizeBytes;
        if (scenario.RecommendedBufferSize > 0)
        {
            // Пользовательская настройка — приоритет; автоопределение служит
            // лишь разумным значением по умолчанию при стандартной настройке.
            if (IoSettings.BufferSizeBytes == 4 * 1024 * 1024)
                buf = scenario.RecommendedBufferSize;
        }

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
                    AddCopiedBytes(delta);
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

        // Проверка целостности. Раньше здесь стояло безусловное
        // item.CopiedBytes = item.SizeBytes — то есть 100% показывалось
        // независимо от того, записано ли что-нибудь. Теперь результат
        // сверяется с фактическим размером файла на диске.
        if (!VerifyDestination(item))
        {
            item.Status = CopyItemStatus.Error;
            throw new IOException(
                $"Файл «{Path.GetFileName(item.SourcePath)}» скопирован с повреждением: " +
                $"ожидалось {item.SizeBytes} байт.");
        }

        // Сверка CRC-32 — только если пользователь её включил.
        // Раньше флажок «Автоматический расчёт CRC-32 на лету» просто
        // записывался в настройки и не читался никем: обещанная защита
        // от повреждений не существовала. Теперь она действительно работает.
        if (IoSettings.VerifyCrc32)
        {
            OnChanged(nameof(CurrentItem));
            var (match, detail) = await Crc32Verifier
                .VerifyMatchAsync(item.SourcePath, item.DestPath, ct)
                .ConfigureAwait(false);

            if (!match)
            {
                item.Status = CopyItemStatus.Error;
                throw new IOException(
                    $"Файл «{Path.GetFileName(item.SourcePath)}» не прошёл проверку целостности: {detail}.");
            }

            item.VerifiedBy = "CRC-32 " + detail.Replace("CRC-32 ", "");
        }

        item.CopiedBytes = item.SizeBytes;
    }

    /// <summary>
    /// Проверяет, что файл назначения существует и его длина совпадает
    /// с исходной. Без этой проверки «успешное» копирование означало лишь
    /// то, что цикл записи завершился без исключения.
    /// </summary>
    private bool VerifyDestination(CopyItem item)
    {
        try
        {
            if (!File.Exists(item.DestPath)) return false;

            long actual = new FileInfo(item.DestPath).Length;
            long expected;
            try { expected = new FileInfo(item.SourcePath).Length; }
            catch { return true; }   // источник исчез: длину сверить не с чем

            // Строгая проверка совпадения длины файла
            return actual == expected;
        }
        catch
        {
            return false;
        }
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

    /// <summary>
    /// Пропустить текущий файл.
    ///
    /// <para>Раньше здесь стояло <c>CopiedBytes += cur.RemainingBytes</c>,
    /// но идущее копирование НЕ останавливалось: телеметрия продолжала
    /// прибавлять те же байты, и прогресс доходил до 150%. Кроме того,
    /// пропущенный файл попадал в Finish(completed: true), а вызывающий
    /// код удалял исходники — то есть удалялось то, что не копировалось.</para>
    ///
    /// <para>Теперь счётчик не трогается: пропущенный файл просто не
    /// учитывается, и прогресс не может превысить фактически
    /// скопированный объём. Отдельный флаг пропуска позволяет вызывающему
    /// коду НЕ удалять исходник пропущенного файла.</para>
    /// </summary>
    public void SkipCurrent()
    {
        var cur = CurrentItem;
        if (cur == null || cur.IsFinished) return;

        cur.Status = CopyItemStatus.Skipped;
        cur.CopiedBytes = 0;
        cur.WasSkipped = true;

        try
        {
            _activeItemCts?.Cancel();
        }
        catch { }

        OnChanged(nameof(CopiedBytes));
        OnChanged(nameof(OverallProgress));
        OnChanged(nameof(CurrentItem));
    }

    /// <summary>
    /// Файлы, которые действительно скопированы и проверены.
    /// Только их исходники можно безопасно удалять при «перемещении».
    /// </summary>
    public List<CopyItem> VerifiedItems =>
        Items.Where(i => i.Status == CopyItemStatus.Done).ToList();

    /// <summary>Был ли хоть один файл помечен пропущенным.</summary>
    public bool AnySkipped => Items.Any(i => i.WasSkipped);

    /// <summary>
    /// Можно ли удалить исходник после «перемещения».
    ///
    /// <para><b>Это защита от безвозвратной потери данных.</b> Раньше вызывающий
    /// код удалял исходник по одному лишь признаку IsCompleted, а тот
    /// выставлялся даже когда: перечисление папки упало на
    /// запрещённом подкаталоге (список оказывался пустым), пользователь
    /// пропустил файл, или копирование прервалось. Итог: «Переместить
    /// папку» превращалось в «Удалить папку».</para>
    ///
    /// <para>Здесь исходник разрешается удалять только если выполнены ВСЕ
    /// условия: копирование действительно завершено, не было отмены,
    /// под этот путь не попал ни один файл со статусом Error или
    /// Skipped, и есть хотя бы один проверенный файл внутри.</para>
    /// </summary>
    public bool CanDeleteSource(string sourcePath)
    {
        if (!IsCompleted || IsCancelled) return false;
        if (string.IsNullOrWhiteSpace(sourcePath)) return false;

        // Для файла: он должен быть среди проверенных и не пропущенным.
        if (File.Exists(sourcePath))
        {
            string full = SafeFull(sourcePath);
            var item = Items.FirstOrDefault(i =>
                string.Equals(SafeFull(i.SourcePath), full, StringComparison.OrdinalIgnoreCase));
            return item != null && item.Status == CopyItemStatus.Done && !item.WasSkipped;
        }

        // Для папки: ВСЕ раскрытые файлы внутри должны быть проверены.
        if (Directory.Exists(sourcePath))
        {
            string root = SafeFull(sourcePath);
            string prefix = root.EndsWith(Path.DirectorySeparatorChar.ToString())
                ? root
                : root + Path.DirectorySeparatorChar;

            var inside = Items
                .Where(i => SafeFull(i.SourcePath).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (inside.Count == 0) return false;
            if (inside.Any(i => i.Status != CopyItemStatus.Done || i.WasSkipped)) return false;
            return true;
        }

        return false;
    }

    private static string SafeFull(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
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

    /// <summary>
    /// Добавление сэмпла скорости под блокировкой.
    /// Список читали UI-потоки во время отрисовки графика, поэтому
    /// обращение к нему из потока конвейера было гонкой данных.
    /// </summary>
    private void PushHistory(double v)
    {
        lock (_historyLock)
        {
            _speedHistory.Add(v);
            if (_speedHistory.Count > MaxHistory) _speedHistory.RemoveAt(0);
        }
    }

    private void Reset()
    {
        _tick.Stop();
        try { _activeItemCts?.Cancel(); } catch { }
        _activeItemCts = null;
        _realCts?.Cancel();
        _realCts?.Dispose();
        _realCts = null;
        Items.Clear();
        lock (_historyLock) _speedHistory.Clear();
        TotalBytes = 0;
        CopiedBytes = 0;
        OperationError = "";
        OnChanged(nameof(OperationError));
        CurrentSpeed = 0;
        _smoothedSpeed = 0;
        _wavePhase = 0;
        _pausedTotal = TimeSpan.Zero;
        _pausedSince = null;
        IsPaused = false;
        IsRunning = false;
        IsCompleted = false;
        IsCancelled = false;
        // Управляющий элемент мог быть уже освобождён предыдущим Dispose().
        try { _pauseGate.Set(); } catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        _tick.Stop();
        try { _activeItemCts?.Cancel(); } catch { }
        _activeItemCts = null;
        _realCts?.Cancel();
        // Порядок важен: сначала отменяем, потом освобождаем управляющий
        // элемент. Иначе работающие потоки конвейера получают
        // ObjectDisposedException на pauseGate.Wait(ct).
        try { _pauseGate.Set(); } catch (ObjectDisposedException) { }
        _pauseGate.Dispose();
        _realCts?.Dispose();
        _realCts = null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
