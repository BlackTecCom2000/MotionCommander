using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Win11CopyDialog.Helpers;
using Win11CopyDialog.Models;
using Win11CopyDialog.Modules.OperationsQueue.Services;

namespace Win11CopyDialog.Modules.OperationsQueue.Views;

public class StringNotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

public partial class OperationsQueueDialog : Window
{
    private readonly UnifiedOperationsQueueService _queue = UnifiedOperationsQueueService.Instance;

    public OperationsQueueDialog()
    {
        InitializeComponent();

        BackdropHelper.Apply(this, ThemeManager.Instance.Backdrop, ThemeManager.Instance.IsDark);

        ActiveItemsControl.ItemsSource = _queue.ActiveOperations;
        HistoryItemsControl.ItemsSource = _queue.HistoryOperations;

        _queue.QueueChanged += Queue_QueueChanged;
        UpdateEmptyPlaceholders();

        Closed += (_, _) =>
        {
            _queue.QueueChanged -= Queue_QueueChanged;
        };
    }

    private void Queue_QueueChanged(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(UpdateEmptyPlaceholders);
    }

    private void UpdateEmptyPlaceholders()
    {
        if (ActiveEmptyPlaceholder != null)
            ActiveEmptyPlaceholder.Visibility = _queue.ActiveOperations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (HistoryEmptyPlaceholder != null)
            HistoryEmptyPlaceholder.Visibility = _queue.HistoryOperations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (ViewActive == null || ViewHistory == null) return;
        HapticAudio.PlayClick();

        ViewActive.Visibility = TabActiveRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ViewHistory.Visibility = TabHistoryRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string id)
        {
            HapticAudio.PlayClick();
            _queue.Pause(id);
        }
    }

    private void Resume_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string id)
        {
            HapticAudio.PlayClick();
            _queue.Resume(id);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string id)
        {
            HapticAudio.PlayClick();
            _queue.Cancel(id);
        }
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string id)
        {
            HapticAudio.PlayClick();
            await _queue.RetryAsync(id);
        }
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        _queue.ClearHistory();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        Close();
    }
}
