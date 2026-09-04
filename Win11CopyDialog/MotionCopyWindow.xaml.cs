using System.Media;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Win11CopyDialog.Helpers;
using Win11CopyDialog.Models;
using Win11CopyDialog.Controls;

using IOPath = System.IO.Path;

namespace Win11CopyDialog;

/// <summary>
/// Motion Copy Engine — премиальное окно копирования: hero-визуализация потока,
/// fluid-прогресс, waveform-график, интерполированные цифры, состояния, микроанимации.
/// API как у CopyDialogWindow: StartSimulation / StartRealCopyAsync / ShowSimulation.
/// </summary>
public partial class MotionCopyWindow : Window
{
    public CopyEngine Engine { get; } = new();
    public bool AutoCloseOnComplete { get; set; }
    public bool PlaySoundOnComplete { get; set; } = true;

    private double _dispPct;
    private double _dispSpeed;
    private string _lastPctText = "";
    private string _lastSpeedText = "";
    private string _lastFile = "";
    private DateTime _lastFrame = DateTime.Now;
    private bool _allowClose;
    private bool _filesExpanded = false;

    public MotionCopyWindow()
    {
        InitializeComponent();
        FilesList.ItemsSource = Engine.Items;
        WaveGraph.Values = Engine.SpeedHistory;
        Engine.ProgressTick += (_, _) => Dispatcher.Invoke(RefreshTargets);
        Engine.Completed += (_, _) => Dispatcher.Invoke(OnCompleted);
        BackdropHelper.Apply(this, ThemeManager.Instance.Backdrop, ThemeManager.Instance.IsDark);

        Loaded += (_, _) =>
        {
            // появление окна: fade + scale (large, 320мс)
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            RootBorder.BeginAnimation(OpacityProperty, fade);
            var sc = new DoubleAnimation(0.965, 1, TimeSpan.FromMilliseconds(320))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            RootScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, sc);
            RootScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, sc);

            _lastFrame = DateTime.Now;
            CompositionTarget.Rendering += OnFrame;
            RefreshTargets();
        };
        Closed += (_, _) => CompositionTarget.Rendering -= OnFrame;
    }

    // ---------- Публичный API ----------

    public void StartSimulation(IEnumerable<(string name, long size)> files, double? speedBytesPerSec = null)
    {
        HideCheck();
        Engine.LoadSimulation(files, speedBytesPerSec);
        WaveGraph.Max = Engine.BaseSpeedBytesPerSec * 1.4;
        RefreshTargets();
    }

    public async Task StartRealCopyAsync(IEnumerable<(string source, string dest)> files, CancellationToken ct = default)
    {
        HideCheck();
        var pairs = files.ToList();
        if (pairs.Count > 0)
        {
            HeroFlow.SourceLabel = pairs.Count == 1 ? IOPath.GetFileName(pairs[0].source.TrimEnd('\\', '/')) : $"{pairs.Count} элементов";
            HeroFlow.DestLabel = IOPath.GetFileName(pairs[0].dest.TrimEnd('\\', '/'));
        }
        WaveGraph.Max = 500 * 1024 * 1024;
        RefreshTargets();
        await Engine.StartRealCopyAsync(pairs, ct);
    }

    public async Task StartRealTransferAsync(string source, string destDir, CancellationToken ct = default)
    {
        HideCheck();
        HeroFlow.SourceLabel = IOPath.GetFileName(source.TrimEnd('\\', '/'));
        HeroFlow.DestLabel = IOPath.GetFileName(destDir.TrimEnd('\\', '/'));
        WaveGraph.Max = 500 * 1024 * 1024;
        RefreshTargets();
        await Engine.StartRealCopyAsync(new[] { (source, destDir) }, ct);
    }

    public async Task StartRealTransferAsync(IEnumerable<string> sources, string destDirectory, CancellationToken ct = default)
    {
        HideCheck();
        var sList = sources.ToList();
        HeroFlow.SourceLabel = sList.Count == 1
            ? IOPath.GetFileName(sList[0].TrimEnd('\\', '/'))
            : $"{sList.Count} элементов";
        HeroFlow.DestLabel = IOPath.GetFileName(destDirectory.TrimEnd('\\', '/'));
        WaveGraph.Max = 500 * 1024 * 1024;
        RefreshTargets();
        var pairs = sList.Select(s => (s, destDirectory)).ToList();
        await Engine.StartRealCopyAsync(pairs, ct);
    }

    public static MotionCopyWindow ShowSimulation(IEnumerable<(string name, long size)> files,
        double? speed = null, bool? topmost = null)
    {
        var w = new MotionCopyWindow();
        if (topmost.HasValue) w.Topmost = topmost.Value;
        w.StartSimulation(files, speed);
        w.Show();
        return w;
    }

    // ---------- Кадр: интерполяция цифр ----------

    private void OnFrame(object? sender, EventArgs e)
    {
        double dt = Math.Min(0.05, (DateTime.Now - _lastFrame).TotalSeconds);
        _lastFrame = DateTime.Now;

        _dispPct = Motion.Damp(_dispPct, Engine.OverallProgress, 6, dt);
        _dispSpeed = Motion.Damp(_dispSpeed, Engine.IsPaused ? 0 : Engine.CurrentSpeed, 5, dt);

        string p = $"{_dispPct:0}%";
        if (p != _lastPctText) { PercentBig.Text = p; _lastPctText = p; }
        string s = Formatters.Speed(_dispSpeed);
        if (s != _lastSpeedText) { SpeedBig.Text = s; _lastSpeedText = s; }
    }

    // ---------- Цели из движка (10 Гц) ----------

    private void RefreshTargets()
    {
        double p = Engine.OverallProgress;
        HeroFlow.Progress = p;
        FluidBar.Value = p;
        FluidBar.IsIndeterminate = !Engine.IsRunning && !Engine.IsCompleted && !Engine.IsCancelled && Engine.Items.Count == 0;

        double refMax = Math.Max(Engine.BaseSpeedBytesPerSec * 1.25,
            Engine.SpeedHistory.DefaultIfEmpty(0).Max() * 1.1);
        if (refMax < 1) refMax = 1;
        HeroFlow.SpeedNorm = Engine.CurrentSpeed / refMax;
        WaveGraph.Max = refMax;

        // состояние
        TransferState st;
        string pill; Color pillC; string hint;
        if (Engine.Items.Any(i => i.Status == CopyItemStatus.Error))
        { st = TransferState.Error; pill = "Ошибка"; pillC = C("#D13438"); hint = "Ошибка записи — проверьте диск и права"; }
        else if (Engine.IsCompleted)
        { st = TransferState.Completed; pill = "Готово"; pillC = C("#107C10"); hint = "Передача завершена"; }
        else if (Engine.IsCancelled)
        { st = TransferState.Paused; pill = "Отменено"; pillC = C("#605E5C"); hint = "Операция отменена"; }
        else if (Engine.IsPaused)
        { st = TransferState.Paused; pill = "Пауза"; pillC = C("#EAA300"); hint = "Поток заморожен — нажмите «Продолжить»"; }
        else if (Engine.IsRunning)
        { st = TransferState.Copying; pill = "Копирование"; pillC = Accent(); hint = "Поток данных активен"; }
        else
        { st = TransferState.Preparing; pill = "Подготовка"; pillC = C("#605E5C"); hint = "Сканирование файлов…"; }

        HeroFlow.State = st;
        PillText.Text = pill;
        if (BeaconDot != null)
        {
            BeaconDot.Fill = new SolidColorBrush(pillC);
            if (BeaconDot.Effect is System.Windows.Media.Effects.DropShadowEffect dse) dse.Color = pillC;
        }
        StatePill.Background = new SolidColorBrush(Color.FromArgb(0x33, pillC.R, pillC.G, pillC.B));
        StatePill.BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, pillC.R, pillC.G, pillC.B));
        StateHint.Text = hint;
        if (FilesCountBadge != null) FilesCountBadge.Text = Engine.Items.Count.ToString();

        // файл с crossfade
        var cur = Engine.CurrentItem;
        string fname = Engine.IsCompleted ? "Все файлы переданы"
            : Engine.IsCancelled ? "Операция отменена"
            : cur != null ? cur.FileName : "Подготовка…";
        if (fname != _lastFile) { _lastFile = fname; AnimateFileChange(fname); }

        RouteText.Text = BuildRoute(cur);
        EtaBig.Text = Engine.IsRunning && !Engine.IsPaused
            ? $"осталось {Formatters.Eta(Engine.Eta)}"
            : Engine.IsCompleted ? $"заняло {Formatters.Elapsed(Engine.Elapsed)}" : "";

        StatTransferred.Text = $"{Formatters.Bytes(Engine.CopiedBytes)}";
        StatRemaining.Text = Formatters.Bytes(Engine.RemainingBytes);
        StatFiles.Text = $"{Engine.DoneCount} / {Engine.Items.Count}";
        StatSpeed.Text = Engine.IsPaused ? "пауза" : Formatters.Speed(Engine.CurrentSpeed);
        StatEta.Text = Engine.IsCompleted ? Formatters.Elapsed(Engine.Elapsed)
            : Engine.IsCancelled ? "—" : Formatters.Eta(Engine.Eta);

        WavePeak.Text = Engine.SpeedHistory.Count > 1
            ? $"пик {Formatters.Speed(Engine.SpeedHistory.Max())}" : "";

        TaskbarInfo.ProgressValue = p / 100.0;
        TaskbarInfo.ProgressState = Engine.IsCancelled ? System.Windows.Shell.TaskbarItemProgressState.Error
            : Engine.Items.Any(i => i.Status == CopyItemStatus.Error) ? System.Windows.Shell.TaskbarItemProgressState.Error
            : Engine.IsPaused ? System.Windows.Shell.TaskbarItemProgressState.Paused
            : Engine.IsCompleted ? System.Windows.Shell.TaskbarItemProgressState.None
            : System.Windows.Shell.TaskbarItemProgressState.Normal;

        bool finished = Engine.IsCompleted || Engine.IsCancelled;
        PauseBtn.Content = Engine.IsPaused ? "▶ Продолжить" : "❚❚ Пауза";
        PauseBtn.IsEnabled = !finished;
        SkipBtn.IsEnabled = !finished;
        CloseBtn.Content = finished ? "Закрыть" : "Отмена";

        static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
        Color Accent() => ThemeManager.Instance.AccentColor;
    }

    private string BuildRoute(CopyItem? cur)
    {
        if (cur != null && !string.IsNullOrEmpty(cur.SourcePath))
            return $"{cur.SourcePath}  →  {cur.DestPath}";
        if (Engine.Items.Count == 0) return "ожидание операции";
        return $"{Engine.DoneCount} из {Engine.Items.Count} • всего {Formatters.Bytes(Engine.TotalBytes)}";
    }

    private void AnimateFileChange(string text)
    {
        // fade out + сдвиг (micro 120мс), замена, fade in (normal 200мс)
        var outAn = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(120));
        var outX = new DoubleAnimation(0, -12, TimeSpan.FromMilliseconds(120));
        outAn.Completed += (_, _) =>
        {
            FileNameText.Text = text;
            FileSlide.X = 12;
            FileBlock.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
            FileSlide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty,
                new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(200))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        };
        FileBlock.BeginAnimation(OpacityProperty, outAn);
        FileSlide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, outX);
    }

    private void OnCompleted()
    {
        RefreshTargets();
        if (Engine.IsCompleted)
        {
            // Плавное скрытие активных цифр скорости и прогресса
            ActiveTelemetry.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(250)));

            // Появление квантового герба завершения
            CompletionStatsText.Text = $"Заняло {Formatters.Elapsed(Engine.Elapsed)} • {Formatters.Bytes(Engine.TotalBytes)}";
            CheckOverlay.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(350)));
            CheckPath.BeginAnimation(System.Windows.Shapes.Path.StrokeDashOffsetProperty,
                new DoubleAnimation(60, 0, TimeSpan.FromMilliseconds(700))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            if (PlaySoundOnComplete)
            {
                HapticAudio.PlaySuccess();
            }
            if (AutoCloseOnComplete)
            {
                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
                t.Tick += (_, _) => { t.Stop(); Close(); };
                t.Start();
            }
        }
    }

    private void HideCheck()
    {
        ActiveTelemetry.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
        CheckOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)));
        CheckPath.BeginAnimation(System.Windows.Shapes.Path.StrokeDashOffsetProperty,
            new DoubleAnimation(60, TimeSpan.FromMilliseconds(1)));
    }

    // ---------- События ----------

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        if (Engine.IsPaused) Engine.Resume(); else Engine.Pause();
        RefreshTargets();
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        Engine.SkipCurrent();
        RefreshTargets();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (Engine.IsCompleted || Engine.IsCancelled) { Close(); return; }
        var r = MessageBox.Show(this, "Отменить операцию копирования?", "Motion Copy Engine",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r == MessageBoxResult.Yes)
        {
            Engine.Cancel();
            RefreshTargets();
        }
    }

    private void FilesToggle_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        _filesExpanded = !_filesExpanded;
        FilesCard.Visibility = _filesExpanded ? Visibility.Visible : Visibility.Collapsed;
        if (FilesArrow != null) FilesArrow.Text = _filesExpanded ? "⌃" : "⌄";
    }

    private void FilesList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (FilesList.SelectedItem is CopyItem item && !item.IsFinished)
        {
            HapticAudio.PlayClick();
            if (ReferenceEquals(item, Engine.CurrentItem)) Engine.SkipCurrent();
            else item.Status = CopyItemStatus.Skipped;
            RefreshTargets();
        }
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        Topmost = !Topmost;
        PinBtn.Opacity = Topmost ? 1 : 0.5;
        PinBtn.Content = Topmost ? "◉" : "◌";
    }

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
        {
            if (Engine.IsRunning && !Engine.IsCompleted && !Engine.IsCancelled)
                Engine.Cancel();
            Engine.Dispose();
            base.OnClosing(e);
            return;
        }
        // быстрое закрытие: fade + scale (micro 140мс)
        e.Cancel = true;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(140));
        fade.Completed += (_, _) => { _allowClose = true; Close(); };
        RootBorder.BeginAnimation(OpacityProperty, fade);
        RootScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, 0.985, TimeSpan.FromMilliseconds(140)));
        RootScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, 0.985, TimeSpan.FromMilliseconds(140)));
    }
}
