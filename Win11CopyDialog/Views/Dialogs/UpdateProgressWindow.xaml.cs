using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Win11CopyDialog.Helpers;
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
        
        string actionPrefix = updateInfo.SwitchMode switch
        {
            VersionSwitchMode.Downgrade => "Откат на версию",
            VersionSwitchMode.Reinstall => "Переустановка версии",
            _ => "Загрузка обновления"
        };
        TitleText.Text = $"{actionPrefix} v{updateInfo.LatestVersion}...";
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _cts = new CancellationTokenSource();
        var progress = new Progress<(long bytesRead, long totalBytes, int percent, double speedMBps)>(p =>
        {
            UpdateProgressBar.SetSafe(p.percent);
            PercentText.Text = $"{p.percent}%";
            double downloadedMb = p.bytesRead / (1024.0 * 1024.0);
            double totalMb = p.totalBytes > 0 ? p.totalBytes / (1024.0 * 1024.0) : 0;
            StatusText.Text = totalMb > 0
                ? $"Скачано {downloadedMb:F1} МБ из {totalMb:F1} МБ ({p.speedMBps:F1} МБ/с)"
                : $"Скачано {downloadedMb:F1} МБ ({p.speedMBps:F1} МБ/с)";
        });

        try
        {
            if (string.IsNullOrWhiteSpace(_updateInfo.PatchUrl) && !string.IsNullOrWhiteSpace(_updateInfo.LatestVersion))
            {
                _updateInfo.PatchUrl = $"https://raw.githubusercontent.com/BlackTecCom2000/MotionCommander/main/dist/MotionCommander-v{_updateInfo.LatestVersion}-Patch.zip";
            }

            bool isPatch = !string.IsNullOrWhiteSpace(_updateInfo.PatchUrl) && _updateInfo.SwitchMode == VersionSwitchMode.Upgrade;
            string url = isPatch ? _updateInfo.PatchUrl : (!string.IsNullOrEmpty(_updateInfo.DownloadUrl) ? _updateInfo.DownloadUrl : _updateInfo.InstallerUrl);

            string actionPrefix = _updateInfo.SwitchMode switch
            {
                VersionSwitchMode.Downgrade => "Откат на версию",
                VersionSwitchMode.Reinstall => "Переустановка версии",
                _ => "Загрузка обновления"
            };

            TitleText.Text = isPatch 
                ? $"Быстрый патч обновления v{_updateInfo.LatestVersion}..." 
                : $"{actionPrefix} v{_updateInfo.LatestVersion}...";

            string downloadedFile;
            try
            {
                downloadedFile = await UpdateService.DownloadUpdateAsync(url, progress, _cts.Token);
            }
            catch when (isPatch && !_cts.IsCancellationRequested && !string.IsNullOrEmpty(_updateInfo.DownloadUrl))
            {
                // Fallback to full download if patch fails
                isPatch = false;
                url = _updateInfo.DownloadUrl;
                TitleText.Text = $"Загрузка полного пакета v{_updateInfo.LatestVersion}...";
                downloadedFile = await UpdateService.DownloadUpdateAsync(url, progress, _cts.Token);
            }
            
            if (downloadedFile.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                StatusText.Text = "Запуск программы установки...";
                UpdateProgressBar.IsIndeterminate = true;
                PercentText.Visibility = Visibility.Collapsed;
                CancelBtn.IsEnabled = false;
                await Task.Delay(500);

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = downloadedFile,
                    UseShellExecute = true,
                    Arguments = "/CLOSEAPPLICATIONS"
                };
                System.Diagnostics.Process.Start(psi);
                Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
                return;
            }

            StatusText.Text = isPatch ? "Применение быстрого патча..." : "Распаковка обновления...";
            UpdateProgressBar.IsIndeterminate = true;
            PercentText.Visibility = Visibility.Collapsed;
            CancelBtn.IsEnabled = false;

            string stagingDir = await UpdateService.PrepareStagingAsync(downloadedFile, _cts.Token);

            StatusText.Text = "Подготовка к бесшовному переходу...";
            await Task.Delay(500);

            var mainWindow = Application.Current.MainWindow as MainWindow;
            AppState state = mainWindow?.GetCurrentState() ?? new AppState();

            // Порядок важен: главное окно запускает анимацию перехода только
            // ПОСЛЕ того, как скрипт обновления успешно запущен. Раньше
            // вызов стоял сразу после ApplySeamlessUpdate, который при
            // отказе в UAC бросал исключение — и анимация не запускалась,
            // что было правильно, но не объяснялось пользователю.
            UpdateService.ApplySeamlessUpdate(stagingDir, state);
            mainWindow?.StartMorphingAnimation();
            this.Close();
        }
        catch (UACDeclinedException ex)
        {
            // Программа остаётся открытой: работа пользователя не потеряна,
            // обновление просто не применилось.
            StatusText.Text = "Требуются права администратора";
            MessageBox.Show(ex.Message, "Обновление не применено",
                MessageBoxButton.OK, MessageBoxImage.Warning);
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

    private void Min_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
    }
}
