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

        VersionTransitionText.Text = $"v{updateInfo.CurrentVersion} ➔ v{updateInfo.LatestVersion}";

        bool isPatch = updateInfo.CanApplyPatch(updateInfo.CurrentVersion);
        if (isPatch)
        {
            PackageBadgeText.Text = "⚡ Быстрый патч";
            int saving = updateInfo.TrafficSavingPercent;
            TrafficSavingText.Text = saving > 0 ? $"Экономия трафика: ~{saving}%" : "";
            TrafficSavingText.Visibility = saving > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            PackageBadgeText.Text = "📦 Полный пакет";
            TrafficSavingText.Visibility = Visibility.Collapsed;
        }

        string actionPrefix = updateInfo.SwitchMode switch
        {
            VersionSwitchMode.Downgrade => "Откат на версию",
            VersionSwitchMode.Reinstall => "Переустановка версии",
            _ => "Загрузка обновления"
        };
        TitleText.Text = isPatch 
            ? $"Быстрый патч v{updateInfo.LatestVersion}..." 
            : $"{actionPrefix} v{updateInfo.LatestVersion}...";
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _cts = new CancellationTokenSource();
        bool isPatch = _updateInfo.CanApplyPatch(_updateInfo.CurrentVersion);

        var progress = new Progress<(long bytesRead, long totalBytes, int percent, double speedMBps)>(p =>
        {
            UpdateProgressBar.SetSafe(p.percent);
            PercentText.Text = $"{p.percent}%";
            double downloadedMb = p.bytesRead / (1024.0 * 1024.0);
            double totalMb = p.totalBytes > 0 ? p.totalBytes / (1024.0 * 1024.0) : 0;
            DownloadStatsText.Text = totalMb > 0
                ? $"{downloadedMb:F1} / {totalMb:F1} МБ ({p.speedMBps:F1} МБ/с)"
                : $"{downloadedMb:F1} МБ ({p.speedMBps:F1} МБ/с)";
            StatusText.Text = isPatch
                ? $"Загрузка патча: {downloadedMb:F1} МБ из {totalMb:F1} МБ..."
                : $"Загрузка полного пакета: {downloadedMb:F1} МБ из {totalMb:F1} МБ...";
        });

        try
        {
            if (string.IsNullOrWhiteSpace(_updateInfo.PatchUrl) && !string.IsNullOrWhiteSpace(_updateInfo.LatestVersion))
            {
                _updateInfo.PatchUrl = $"https://raw.githubusercontent.com/BlackTecCom2000/MotionCommander/main/dist/MotionCommander-v{_updateInfo.LatestVersion}-Patch.zip";
            }

            string url = isPatch ? _updateInfo.PatchUrl : (!string.IsNullOrEmpty(_updateInfo.DownloadUrl) ? _updateInfo.DownloadUrl : _updateInfo.InstallerUrl);
            string stagingDir;
            string downloadedFile;

            try
            {
                downloadedFile = await UpdateService.DownloadUpdateAsync(url, progress, _cts.Token);

                if (isPatch)
                {
                    StatusText.Text = "Проверка совместимости и распаковка патча...";
                    UpdateProgressBar.IsIndeterminate = true;
                    PercentText.Visibility = Visibility.Collapsed;
                    CancelBtn.IsEnabled = false;

                    stagingDir = await UpdateService.PrepareStagingAsync(downloadedFile, _cts.Token);
                    StatusText.Text = "Подготовка к бесшовному применению патча...";
                }
                else
                {
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

                    StatusText.Text = "Распаковка полного пакета...";
                    UpdateProgressBar.IsIndeterminate = true;
                    PercentText.Visibility = Visibility.Collapsed;
                    CancelBtn.IsEnabled = false;

                    stagingDir = await UpdateService.PrepareStagingAsync(downloadedFile, _cts.Token);
                }
            }
            catch (Exception ex) when (isPatch && !_cts.IsCancellationRequested && (!string.IsNullOrEmpty(_updateInfo.DownloadUrl) || !string.IsNullOrEmpty(_updateInfo.InstallerUrl)))
            {
                // Если патч повреждён, несовместим или не скачался — честно переключаем на полный пакет (fallback)
                isPatch = false;
                _updateInfo.PackageType = UpdatePackageType.FullPackage;

                FallbackNoticeText.Text = $"Патч не удалось применить ({ex.Message}). Выполняется загрузка полного установочного пакета.";
                FallbackNoticeCard.Visibility = Visibility.Visible;
                PackageBadgeText.Text = "📦 Полный пакет";
                TrafficSavingText.Text = "Запасной вариант (полная установка)";
                TrafficSavingText.Visibility = Visibility.Visible;
                TitleText.Text = $"Загрузка полного пакета v{_updateInfo.LatestVersion}...";
                StatusText.Text = "Загрузка полного пакета...";
                UpdateProgressBar.IsIndeterminate = false;
                UpdateProgressBar.Value = 0;
                PercentText.Text = "0%";
                PercentText.Visibility = Visibility.Visible;
                DownloadStatsText.Text = "";
                CancelBtn.IsEnabled = true;

                url = !string.IsNullOrEmpty(_updateInfo.DownloadUrl) ? _updateInfo.DownloadUrl : _updateInfo.InstallerUrl;
                downloadedFile = await UpdateService.DownloadUpdateAsync(url, progress, _cts.Token);

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

                StatusText.Text = "Распаковка полного пакета...";
                UpdateProgressBar.IsIndeterminate = true;
                PercentText.Visibility = Visibility.Collapsed;
                CancelBtn.IsEnabled = false;

                stagingDir = await UpdateService.PrepareStagingAsync(downloadedFile, _cts.Token);
            }

            StatusText.Text = isPatch 
                ? "Применение патча и подготовка к перезапуску..." 
                : "Применение обновления и подготовка к перезапуску...";
            await Task.Delay(500);

            var mainWindow = Application.Current.MainWindow as MainWindow;
            AppState state = mainWindow?.GetCurrentState() ?? new AppState();

            // Порядок важен: анимация перехода запускается только ПОСЛЕ успешного старта скрипта
            UpdateService.ApplySeamlessUpdate(stagingDir, state);
            mainWindow?.StartMorphingAnimation();
            this.Close();
        }
        catch (UACDeclinedException ex)
        {
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
