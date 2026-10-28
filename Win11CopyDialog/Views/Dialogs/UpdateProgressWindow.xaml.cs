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
    private CancellationTokenSource? _fallbackDelayCts;

    /// <summary>
    /// Является ли текущая загрузка патчем.
    /// </summary>
    /// <remarks>
    /// Признак берётся из <see cref="UpdateInfo.PackageType"/>, а не из
    /// режима смены версии: режим отвечает на вопрос «выше или ниже»,
    /// а патч — это свойство пакета. При выборе версии вручную патч мог
    /// скачиваться как «полный пакет» и наоборот: подпись в окне и
    /// запасной путь расходились с реально загружаемым файлом.
    /// </remarks>
    private bool IsPatchPackage =>
        _updateInfo.PackageType == UpdatePackageType.Patch && _updateInfo.HasPatch;

    public UpdateProgressWindow(UpdateInfo updateInfo)
    {
        InitializeComponent();
        _updateInfo = updateInfo;

        VersionTransitionText.Text = $"v{updateInfo.CurrentVersion} ➔ v{updateInfo.LatestVersion}";

        bool isPatch = IsPatchPackage;
        if (isPatch)
        {
            PackageBadgeText.Text = "⚡ Быстрый патч";
            int saving = updateInfo.TrafficSavingPercent;
            TrafficSavingText.Text = saving > 0 ? $"Экономия трафика: ~{saving}%" : "Обновление файлов программы";
            TrafficSavingText.Visibility = Visibility.Visible;
            TitleText.Text = $"Быстрый патч v{updateInfo.LatestVersion}...";
        }
        else
        {
            PackageBadgeText.Text = "📦 Полный пакет";
            TrafficSavingText.Text = "Обновление поверх установленной версии";
            TrafficSavingText.Visibility = Visibility.Visible;
            TitleText.Text = $"Обновление программы v{updateInfo.LatestVersion}...";
        }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _cts = new CancellationTokenSource();
        bool isPatch = IsPatchPackage;

        string packageLabel = isPatch ? "патч" : "полный пакет";
        UpdateLog.Info("ui",
            $"Обновление v{_updateInfo.CurrentVersion} → v{_updateInfo.LatestVersion}: " +
            $"{packageLabel}, режим {_updateInfo.SwitchMode}");

        var progress = new Progress<(long bytesRead, long totalBytes, int percent, double speedMBps)>(p =>
        {
            UpdateProgressBar.SetSafe(p.percent);
            PercentText.Text = $"{p.percent}%";
            double downloadedMb = p.bytesRead / (1024.0 * 1024.0);
            double totalMb = p.totalBytes > 0 ? p.totalBytes / (1024.0 * 1024.0) : 0;

            // Остаток считается по ИЗМЕРЕННОЙ скорости текущей сессии,
            // а не по предполагаемой скорости канала: только такая оценка
            // не врёт, когда канал просел или загрузка возобновилась.
            string etaText = "";
            if (totalMb > 0 && p.speedMBps > 0.01)
            {
                double remainingMb = Math.Max(0, totalMb - downloadedMb);
                int etaSeconds = (int)Math.Ceiling(remainingMb / p.speedMBps);
                etaText = $" · осталось ~{FormatDuration(etaSeconds)}";
            }

            DownloadStatsText.Text = totalMb > 0
                ? $"{downloadedMb:F1} / {totalMb:F1} МБ ({p.speedMBps:F1} МБ/с){etaText}"
                : $"{downloadedMb:F1} МБ ({p.speedMBps:F1} МБ/с)";

            // Сервер не сообщил полный размер — «из 0.0 МБ» было бы враньём.
            StatusText.Text = totalMb > 0
                ? (isPatch
                    ? $"Загрузка патча: {downloadedMb:F1} МБ из {totalMb:F1} МБ..."
                    : $"Загрузка полного пакета: {downloadedMb:F1} МБ из {totalMb:F1} МБ...")
                : (isPatch
                    ? $"Загрузка патча: {downloadedMb:F1} МБ..."
                    : $"Загрузка полного пакета: {downloadedMb:F1} МБ...");
        });

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (isPatch)
                {
                    TitleText.Text = $"Быстрый патч v{_updateInfo.LatestVersion}...";
                    PackageBadgeText.Text = "⚡ Быстрый патч";
                    int saving = _updateInfo.TrafficSavingPercent;
                    TrafficSavingText.Text = saving > 0 ? $"Экономия трафика: ~{saving}%" : "Обновление файлов программы";
                    TrafficSavingText.Visibility = Visibility.Visible;
                    StatusText.Text = "Загрузка быстрого патча...";
                }
                else
                {
                    TitleText.Text = $"Обновление программы v{_updateInfo.LatestVersion}...";
                    PackageBadgeText.Text = "📦 Полный пакет";
                    TrafficSavingText.Text = "Обновление поверх установленной версии";
                    TrafficSavingText.Visibility = Visibility.Visible;
                    StatusText.Text = "Загрузка полного пакета обновления...";
                }

                UpdateProgressBar.IsIndeterminate = false;
                PercentText.Visibility = Visibility.Visible;
                CancelBtn.IsEnabled = true;

                string url = isPatch 
                    ? _updateInfo.GetEffectivePatchUrl() 
                    : _updateInfo.GetEffectiveFullZipUrl();

                string downloadedFile = await UpdateService.DownloadUpdateAsync(
                    url,
                    progress,
                    _cts.Token,
                    expectedBytes: (long)Math.Round(_updateInfo.DownloadSizeMb * 1024 * 1024));
                string stagingDir;

                StatusText.Text = isPatch
                    ? "Проверка целостности и распаковка патча..."
                    : "Проверка целостности и распаковка полного пакета...";
                UpdateProgressBar.IsIndeterminate = true;
                PercentText.Visibility = Visibility.Collapsed;
                CancelBtn.IsEnabled = false;

                stagingDir = await UpdateService.PrepareStagingAsync(
                    downloadedFile, _cts.Token, _updateInfo.LatestVersion);

                // Когда загрузилось и распаковано — программа закрывается и обновляется поверх файлов
                StatusText.Text = isPatch 
                    ? "Патч готов к применению. Закрытие программы для обновления поверх файлов..." 
                    : "Обновление готово к применению. Закрытие программы для обновления поверх файлов...";
                await Task.Delay(800, _cts.Token);

                var mainWindow = Application.Current.MainWindow as MainWindow;
                AppState state = mainWindow?.GetCurrentState() ?? new AppState();

                // Порядок: запускаем бесшовное применение поверх приложения и закрываем текущее окно
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
                // Не удалось применить патч: информируем пользователя и автоматически переходим к полной версии поверх приложения
                string userReason = ex switch
                {
                    UpdateIntegrityException uie => $"Контрольная сумма патча не совпала: {uie.Message}",
                    PatchIncompatibleException pie => $"Несовместимость патча: {pie.Message}",
                    IOException ioe when ioe.Message.Contains("занят") || ioe.Message.Contains("used by another process") || ioe.Message.Contains("sharing violation") =>
                        "Файл патча был временно заблокирован другой программой или антивирусом.",
                    _ => ex.Message
                };

                // Размер берётся из манифеста. Если его там нет (ответ пришёл
                // от GitHub API, а не от version.json), цифра не выдумывается:
                // пользователь увидит «уточнится при загрузке», а точные
                // значения появятся из заголовков ответа.
                double fullSizeMb = _updateInfo.FullSizeMb;
                FallbackNoticeText.Text = $"Быстрый патч не удалось применить ({userReason}). Автоматический переход к загрузке полной версии программы поверх текущей установки.";
                FallbackPackageSizeText.Text = fullSizeMb > 0
                    ? $"Размер полного архива: ~{fullSizeMb:F1} МБ. Файлы программы сохранены без изменений."
                    : "Размер полного архива уточнится при загрузке. Файлы программы сохранены без изменений.";
                DownloadFullBtn.Content = fullSizeMb > 0
                    ? $"📦 Скачать полный пакет (~{fullSizeMb:F1} МБ)"
                    : "📦 Скачать полный пакет";

                UpdateLog.Warn("ui", $"Патч не применён, переход к полному пакету: {userReason}");

                FallbackNoticeCard.Visibility = Visibility.Visible;
                StatusText.Text = "Переход к загрузке полной программы...";
                UpdateProgressBar.IsIndeterminate = false;
                UpdateProgressBar.Value = 0;
                PercentText.Visibility = Visibility.Collapsed;
                DownloadStatsText.Text = "";
                CancelBtn.IsEnabled = true;

                _fallbackDelayCts = new CancellationTokenSource();
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, _fallbackDelayCts.Token);

                try
                {
                    // Пауза 2.5 сек, чтобы пользователь успел увидеть причину перехода
                    await Task.Delay(2500, linkedCts.Token);
                }
                catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
                {
                    // Пользователь нажал кнопку немедленного перехода
                }

                FallbackNoticeCard.Visibility = Visibility.Collapsed;
                isPatch = false;
                _updateInfo.PackageType = UpdatePackageType.FullPackage;
                continue;
            }
            catch (Exception ex) when (!_cts.IsCancellationRequested)
            {
                UpdateLog.Error("ui", $"Обновление не выполнено: {ex}");
                MessageBox.Show(
                    $"Не удалось выполнить обновление программы:\n{ex.Message}\n\nТекущая установленная версия программы не была затронута и продолжает работать.\n\n" +
                    $"Подробный журнал обновления:\n{UpdateLog.LogFilePath}", 
                    "Ошибка обновления", MessageBoxButton.OK, MessageBoxImage.Error);
                this.Close();
                return;
            }
        }
    }

    /// <summary>Длительность в человекочитаемом виде для оценки остатка.</summary>
    private static string FormatDuration(int seconds)
    {
        if (seconds < 60) return $"{seconds} с";

        int minutes = seconds / 60;
        int restSeconds = seconds % 60;
        if (minutes < 60) return restSeconds > 0 ? $"{minutes} мин {restSeconds} с" : $"{minutes} мин";

        int hours = minutes / 60;
        int restMinutes = minutes % 60;
        return restMinutes > 0 ? $"{hours} ч {restMinutes} мин" : $"{hours} ч";
    }

    private void RetryPatchBtn_Click(object sender, RoutedEventArgs e)
    {
        FallbackNoticeCard.Visibility = Visibility.Collapsed;
        _fallbackDelayCts?.Cancel();
    }

    private void DownloadFullBtn_Click(object sender, RoutedEventArgs e)
    {
        FallbackNoticeCard.Visibility = Visibility.Collapsed;
        _fallbackDelayCts?.Cancel();
    }

    private void Min_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        _fallbackDelayCts?.Cancel();
        _cts?.Cancel();
    }

    protected override void OnClosed(EventArgs e)
    {
        _fallbackDelayCts?.Cancel();
        _cts?.Cancel();
        base.OnClosed(e);
    }
}
