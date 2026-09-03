using System.Media;
using System.Windows;
using Win11CopyDialog.Helpers;
using Win11CopyDialog.Models;

namespace Win11CopyDialog;

/// <summary>
/// Универсальное окно копирования в стиле Windows 11.
/// Использование:
///   var dlg = new CopyDialogWindow();
///   dlg.StartSimulation(files, speedBytesPerSec);
///   dlg.Show();
/// или: CopyDialogWindow.ShowSimulation(...), ShowRealCopyAsync(...)
/// </summary>
public partial class CopyDialogWindow : Window
{
    public CopyEngine Engine { get; } = new();
    public bool AutoCloseOnComplete { get; set; }
    public bool PlaySoundOnComplete { get; set; } = true;
    public bool IsDetailsExpanded { get; private set; }

    private bool _closeRequested;

    public CopyDialogWindow()
    {
        InitializeComponent();
        FilesList.ItemsSource = Engine.Items;
        Graph.Values = Engine.SpeedHistory;
        Engine.ProgressTick += (_, _) => Dispatcher.Invoke(RefreshUi);
        Engine.Completed += (_, _) => Dispatcher.Invoke(OnCompleted);
        ThemeManager.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ThemeManager.AccentColor)) Graph.InvalidateVisual();
        };
        BackdropHelper.Apply(this, ThemeManager.Instance.Backdrop, ThemeManager.Instance.IsDark);
        RefreshUi();
    }

    // ---------- Публичный API ----------

    public void StartSimulation(IEnumerable<(string name, long size)> files, double? speedBytesPerSec = null)
    {
        ResetButtons();
        Engine.LoadSimulation(files, speedBytesPerSec);
        Graph.Max = Engine.BaseSpeedBytesPerSec * 1.6;
        RefreshUi();
    }

    public async Task StartRealCopyAsync(IEnumerable<(string source, string dest)> files, CancellationToken ct = default)
    {
        ResetButtons();
        Graph.Max = Engine.BaseSpeedBytesPerSec * 1.6;
        RefreshUi();
        await Engine.StartRealCopyAsync(files, ct);
    }

    public static CopyDialogWindow ShowSimulation(IEnumerable<(string name, long size)> files,
        double? speed = null, bool details = false, bool? topmost = null)
    {
        var w = new CopyDialogWindow();
        if (topmost.HasValue) w.Topmost = topmost.Value;
        w.StartSimulation(files, speed);
        if (details) w.SetDetails(true);
        w.Show();
        return w;
    }

    public void SetDetails(bool expanded)
    {
        IsDetailsExpanded = expanded;
        DetailsPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        DetailsButton.Content = expanded ? "Скрыть детали ⌃" : "Подробнее ⌄";
        Height = expanded ? 640 : 348;
        MinHeight = expanded ? 400 : 200;
    }

    // ---------- UI ----------

    private void RefreshUi()
    {
        double p = Engine.OverallProgress;
        PercentText.Text = Engine.IsCompleted ? "100% завершено"
            : Engine.IsCancelled ? "Отменено"
            : $"{p:0}% завершено";
        TitleText.Text = Engine.IsCompleted ? "Копирование завершено"
            : Engine.IsCancelled ? "Копирование отменено"
            : $"Копирование • {p:0}%";

        var cur = Engine.CurrentItem;
        FileNameText.Text = Engine.IsCompleted ? "Все файлы скопированы ✔"
            : Engine.IsCancelled ? "Операция отменена"
            : cur != null ? $"Копирование: {cur.FileName}" : "Подготовка…";
        PathText.Text = cur != null && !string.IsNullOrEmpty(cur.SourcePath)
            ? $"{cur.SourcePath}  →  {cur.DestPath}"
            : $"{Engine.DoneCount} из {Engine.Items.Count} элементов";

        SpeedRun.Text = Engine.IsPaused ? "пауза" : Formatters.Speed(Engine.CurrentSpeed);
        EtaRun.Text = Engine.IsCompleted ? "готово" : Engine.IsCancelled ? "—" : Formatters.Eta(Engine.Eta);

        TotalProgress.Value = p;
        TaskbarInfo.ProgressValue = p / 100.0;
        TaskbarInfo.ProgressState = Engine.IsCancelled ? System.Windows.Shell.TaskbarItemProgressState.Error
            : Engine.IsPaused ? System.Windows.Shell.TaskbarItemProgressState.Paused
            : Engine.IsCompleted ? System.Windows.Shell.TaskbarItemProgressState.None
            : System.Windows.Shell.TaskbarItemProgressState.Normal;

        if (cur != null) FileProgress.Value = cur.Progress;
        ItemsRun.Text = $"{Engine.DoneCount} / {Engine.Items.Count}";
        BytesRun.Text = $"{Formatters.Bytes(Engine.CopiedBytes)} из {Formatters.Bytes(Engine.TotalBytes)}";
        ElapsedRun.Text = Formatters.Elapsed(Engine.Elapsed);
        LeftRun.Text = Engine.IsCompleted ? "—" : Formatters.Bytes(Engine.RemainingBytes);

        Graph.Max = Math.Max(Graph.Max, Engine.SpeedHistory.DefaultIfEmpty(0).Max() * 1.15);
        Graph.InvalidateVisual();

        string pauseGlyph = Engine.IsPaused ? "▶" : "⏸";
        PauseButton.Content = pauseGlyph;
        PauseButton.ToolTip = Engine.IsPaused ? "Продолжить" : "Приостановить";
        PauseBottomButton.Content = Engine.IsPaused ? "Продолжить" : "Пауза";

        bool finished = Engine.IsCompleted || Engine.IsCancelled;
        PauseButton.IsEnabled = !finished;
        PauseBottomButton.IsEnabled = !finished;
        SkipButton.IsEnabled = !finished;
        CloseButton.Content = finished ? "Закрыть" : "Отмена";
        CancelTopButton.ToolTip = finished ? "Закрыть" : "Отменить";
    }

    private void OnCompleted()
    {
        RefreshUi();
        if (Engine.IsCompleted && PlaySoundOnComplete)
        {
            HapticAudio.PlaySuccess();
        }
        if (Engine.IsCompleted && AutoCloseOnComplete)
        {
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            t.Tick += (_, _) => { t.Stop(); Close(); };
            t.Start();
        }
    }

    private void ResetButtons()
    {
        _closeRequested = false;
        PauseButton.IsEnabled = true;
        PauseBottomButton.IsEnabled = true;
        SkipButton.IsEnabled = true;
    }

    // ---------- События ----------

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        if (Engine.IsPaused) Engine.Resume(); else Engine.Pause();
        RefreshUi();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        if (Engine.IsCompleted || Engine.IsCancelled) { Close(); return; }
        var r = MessageBox.Show(this, "Отменить операцию копирования?", "Копирование",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r == MessageBoxResult.Yes)
        {
            Engine.Cancel();
            RefreshUi();
        }
    }

    private void SkipButton_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        Engine.SkipCurrent();
        RefreshUi();
    }

    private void DetailsButton_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        SetDetails(!IsDetailsExpanded);
    }

    private void FilesList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (FilesList.SelectedItem is Models.CopyItem item && !item.IsFinished)
        {
            HapticAudio.PlayClick();
            if (ReferenceEquals(item, Engine.CurrentItem)) Engine.SkipCurrent();
            else item.Status = Models.CopyItemStatus.Skipped;
            RefreshUi();
        }
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        Topmost = !Topmost;
        PinButton.Opacity = Topmost ? 1 : 0.5;
    }

    private void MinButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!_closeRequested && Engine.IsRunning && !Engine.IsCompleted && !Engine.IsCancelled)
            Engine.Cancel();
        Engine.Dispose();
    }
}
