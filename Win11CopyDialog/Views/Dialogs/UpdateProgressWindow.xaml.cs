using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Win11CopyDialog.Modules.UpdateEngine;

namespace Win11CopyDialog.Views.Dialogs;

public partial class UpdateProgressWindow : Window
{
    private readonly UpdateInfo _updateInfo;
    private CancellationTokenSource? _cts;

    public UpdateProgressWindow(UpdateInfo updateInfo)
    {
        InitializeComponent();
        _updateInfo = updateInfo;
        TitleText.Text = $"Загрузка обновления v{updateInfo.LatestVersion}...";
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _cts = new CancellationTokenSource();
        var progress = new Progress<(long bytesRead, long totalBytes, int percent, double speedMBps)>(p =>
        {
            UpdateProgressBar.Value = p.percent;
            PercentText.Text = $"{p.percent}%";
            StatusText.Text = $"Скачано {p.bytesRead / 1024 / 1024} МБ из {p.totalBytes / 1024 / 1024} МБ ({p.speedMBps:F1} МБ/с)";
        });

        try
        {
            string url = !string.IsNullOrEmpty(_updateInfo.InstallerUrl) ? _updateInfo.InstallerUrl : _updateInfo.DownloadUrl;
            string downloadedFile = await UpdateService.DownloadUpdateAsync(url, progress, _cts.Token);
            
            StatusText.Text = "Распаковка обновления...";
            UpdateProgressBar.IsIndeterminate = true;
            PercentText.Visibility = Visibility.Collapsed;
            CancelBtn.IsEnabled = false;

            string stagingDir = await UpdateService.PrepareStagingAsync(downloadedFile, _cts.Token);

            StatusText.Text = "Подготовка к бесшовному переходу...";
            await Task.Delay(500);

            var mainWindow = Application.Current.MainWindow as MainWindow;
            AppState state = mainWindow?.GetCurrentState() ?? new AppState();

            UpdateService.ApplySeamlessUpdate(stagingDir, state);
            mainWindow?.StartMorphingAnimation();
            this.Close();
        }
        catch (OperationCanceledException)
        {
            MessageBox.Show("Загрузка обновления отменена.", "Отмена", MessageBoxButton.OK, MessageBoxImage.Information);
            this.Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка загрузки обновления:\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            this.Close();
        }
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
    }
}
