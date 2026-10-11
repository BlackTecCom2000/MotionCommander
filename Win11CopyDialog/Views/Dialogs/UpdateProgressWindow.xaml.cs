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

    private enum FallbackUserChoice
    {
        RetryPatch,
        DownloadFull,
        Cancel
    }

    private TaskCompletionSource<FallbackUserChoice>? _fallbackChoiceTcs;

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

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_updateInfo.PatchUrl) && !string.IsNullOrWhiteSpace(_updateInfo.LatestVersion))
                {
                    _updateInfo.PatchUrl = $"https://raw.githubusercontent.com/BlackTecCom2000/MotionCommander/main/dist/MotionCommander-v{_updateInfo.LatestVersion}-Patch.zip";
                }

                string url = isPatch 
                    ? _updateInfo.PatchUrl 
                    : (!string.IsNullOrEmpty(_updateInfo.DownloadUrl) ? _updateInfo.DownloadUrl : _updateInfo.InstallerUrl);

                StatusText.Text = isPatch ? "Загрузка быстрого патча..." : "Загрузка полного пакета...";
                UpdateProgressBar.IsIndeterminate = false;
                PercentText.Visibility = Visibility.Visible;
                CancelBtn.IsEnabled = true;

                string downloadedFile = await UpdateService.DownloadUpdateAsync(url, progress, _cts.Token);
                string stagingDir;

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
                return;
            }
            catch (UACDeclinedException ex)
            {
                StatusText.Text = "Требуются права администратора";
                MessageBox.Show(ex.Message, "Обновление не применено",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                this.Close();
                return;
            }
            catch (OperationCanceledException)
            {
                this.Close();
                return;
            }
            catch (Exception ex) when (isPatch && !_cts.IsCancellationRequested)
            {
                // Не скачиваем полный пакет молча и без согласия!
                // Показываем пользователю понятную причину, размер пакета и выбор действий.
                string userReason = ex switch
                {
                    UpdateIntegrityException uie => $"Контрольная сумма файла не совпала: {uie.Message}",
                    PatchIncompatibleException pie => $"Несовместимость патча: {pie.Message}",
                    IOException ioe when ioe.Message.Contains("занят") || ioe.Message.Contains("used by another process") || ioe.Message.Contains("sharing violation") =>
                        "Файл обновления был временно занят другой программой или антивирусом.",
                    _ => ex.Message
                };

                double fullSizeMb = _updateInfo.FullSizeMb > 0 ? _updateInfo.FullSizeMb : 40.5;
                FallbackNoticeText.Text = $"{userReason} Исходные файлы программы не повреждены и сохранены без изменений.";
                FallbackPackageSizeText.Text = $"Размер полного установочного пакета: ~{fullSizeMb:F1} МБ (вместо патча {_updateInfo.PatchSizeMb:F1} МБ).";
                DownloadFullBtn.Content = $"📦 Скачать полный пакет (~{fullSizeMb:F1} МБ)";

                FallbackNoticeCard.Visibility = Visibility.Visible;
                StatusText.Text = "Ожидание решения пользователя...";
                UpdateProgressBar.IsIndeterminate = false;
                UpdateProgressBar.Value = 0;
                PercentText.Visibility = Visibility.Collapsed;
                DownloadStatsText.Text = "";
                CancelBtn.IsEnabled = true;

                _fallbackChoiceTcs = new TaskCompletionSource<FallbackUserChoice>();
                using var reg = _cts.Token.Register(() => _fallbackChoiceTcs.TrySetResult(FallbackUserChoice.Cancel));

                var choice = await _fallbackChoiceTcs.Task;
                FallbackNoticeCard.Visibility = Visibility.Collapsed;

                if (choice == FallbackUserChoice.RetryPatch)
                {
                    isPatch = true;
                    StatusText.Text = "Повторная попытка применения патча...";
                    PercentText.Visibility = Visibility.Visible;
                    continue;
                }
                else if (choice == FallbackUserChoice.DownloadFull)
                {
                    isPatch = false;
                    _updateInfo.PackageType = UpdatePackageType.FullPackage;
                    PackageBadgeText.Text = "📦 Полный пакет";
                    TrafficSavingText.Text = "Полная установка (запасной вариант)";
                    TrafficSavingText.Visibility = Visibility.Visible;
                    TitleText.Text = $"Загрузка полного пакета v{_updateInfo.LatestVersion}...";
                    StatusText.Text = "Загрузка полного пакета...";
                    PercentText.Visibility = Visibility.Visible;
                    continue;
                }
                else
                {
                    this.Close();
                    return;
                }
            }
            catch (Exception ex) when (!_cts.IsCancellationRequested)
            {
                MessageBox.Show($"Ошибка при выполнении обновления:\n{ex.Message}", "Ошибка обновления", MessageBoxButton.OK, MessageBoxImage.Error);
                this.Close();
                return;
            }
        }
    }

    private void RetryPatchBtn_Click(object sender, RoutedEventArgs e)
    {
        _fallbackChoiceTcs?.TrySetResult(FallbackUserChoice.RetryPatch);
    }

    private void DownloadFullBtn_Click(object sender, RoutedEventArgs e)
    {
        _fallbackChoiceTcs?.TrySetResult(FallbackUserChoice.DownloadFull);
    }

    private void Min_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        _fallbackChoiceTcs?.TrySetResult(FallbackUserChoice.Cancel);
        _cts?.Cancel();
    }

    protected override void OnClosed(EventArgs e)
    {
        _fallbackChoiceTcs?.TrySetResult(FallbackUserChoice.Cancel);
        _cts?.Cancel();
        base.OnClosed(e);
    }
}
