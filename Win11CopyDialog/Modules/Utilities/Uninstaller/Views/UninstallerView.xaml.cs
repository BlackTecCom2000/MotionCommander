using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Win11CopyDialog.Helpers;
using Win11CopyDialog.Modules.Utilities.Uninstaller.Models;
using Win11CopyDialog.Modules.Utilities.Uninstaller.Services;

namespace Win11CopyDialog.Modules.Utilities.Uninstaller.Views
{
    public partial class UninstallerView : UserControl
    {
        private readonly ApplicationDiscoveryService _discoveryService;
        private readonly ApplicationRemovalService _removalService;
        private List<InstalledApplication> _allApps;
        private List<ResidualItem> _currentResiduals;

        public UninstallerView()
        {
            InitializeComponent();
            
            _discoveryService = new ApplicationDiscoveryService();
            _removalService = new ApplicationRemovalService();
            _allApps = new List<InstalledApplication>();
            _currentResiduals = new List<ResidualItem>();

            Loaded += UninstallerView_Loaded;
        }

        private async void UninstallerView_Loaded(object sender, RoutedEventArgs e)
        {
            if (_allApps.Count == 0)
            {
                await LoadApplicationsAsync();
            }
        }

        private async Task LoadApplicationsAsync()
        {
            StatusStatsText.Text = "Сбор информации об установленных приложениях...";
            RefreshBtn.IsEnabled = false;

            try
            {
                _allApps = await Task.Run(() => _discoveryService.GetInstalledApplications());
                FilterApps();
            }
            catch (Exception ex)
            {
                StatusStatsText.Text = $"Ошибка при загрузке: {ex.Message}";
            }
            finally
            {
                RefreshBtn.IsEnabled = true;
            }
        }

        private void FilterApps()
        {
            if (_allApps == null) return;

            string query = SearchBox.Text?.Trim().ToLowerInvariant() ?? "";
            var filtered = _allApps.AsEnumerable();

            // 1. Поиск
            if (!string.IsNullOrEmpty(query))
            {
                filtered = filtered.Where(a => 
                    (a.DisplayName?.ToLowerInvariant().Contains(query) ?? false) ||
                    (a.Publisher?.ToLowerInvariant().Contains(query) ?? false) ||
                    (a.DisplayVersion?.ToLowerInvariant().Contains(query) ?? false));
            }

            // 2. Категории
            if (FilterLargeRadio.IsChecked == true)
            {
                filtered = filtered.Where(a => a.SizeBytes >= 500L * 1024L * 1024L);
            }
            else if (FilterRecentRadio.IsChecked == true)
            {
                var cutoff = DateTime.Now.AddDays(-30);
                filtered = filtered.Where(a => a.InstallDateParsed.HasValue && a.InstallDateParsed.Value >= cutoff);
            }
            else if (FilterOrphanedRadio.IsChecked == true)
            {
                filtered = filtered.Where(a => a.IsOrphaned);
            }
            else if (FilterSystemRadio.IsChecked == true)
            {
                filtered = filtered.Where(a => a.IsSystemComponent);
            }
            else
            {
                // По умолчанию скрываем системные компоненты в общем списке "Все", если нет прямого поиска
                if (string.IsNullOrEmpty(query))
                {
                    filtered = filtered.Where(a => !a.IsSystemComponent);
                }
            }

            // 3. Сортировка
            int sortIndex = SortComboBox?.SelectedIndex ?? 0;
            filtered = sortIndex switch
            {
                0 => filtered.OrderBy(a => a.DisplayName),
                1 => filtered.OrderByDescending(a => a.DisplayName),
                2 => filtered.OrderByDescending(a => a.SizeBytes),
                3 => filtered.OrderBy(a => a.SizeBytes > 0 ? a.SizeBytes : long.MaxValue),
                4 => filtered.OrderByDescending(a => a.InstallDateParsed ?? DateTime.MinValue),
                5 => filtered.OrderBy(a => a.Publisher),
                _ => filtered.OrderBy(a => a.DisplayName)
            };

            var resultList = filtered.ToList();
            AppsList.ItemsSource = resultList;

            // Расчет статистики
            long totalBytes = _allApps.Where(a => a.SizeBytes > 0).Sum(a => a.SizeBytes);
            int selectedCount = _allApps.Count(a => a.IsSelected);
            string totalSizeStr = ApplicationDiscoveryService.FormatBytes(totalBytes);

            StatusStatsText.Text = $"Всего программ: {_allApps.Count} • Занимают: {totalSizeStr} • Показано: {resultList.Count} • Выбрано: {selectedCount}";

            UpdateBatchButton();
        }

        private void UpdateBatchButton()
        {
            int selectedCount = _allApps.Count(a => a.IsSelected);
            if (selectedCount > 0)
            {
                BatchUninstallBtn.Visibility = Visibility.Visible;
                BatchUninstallBtn.Content = $"🗑 Удалить выбранные ({selectedCount})";
            }
            else
            {
                BatchUninstallBtn.Visibility = Visibility.Collapsed;
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => FilterApps();
        private void FilterRadio_Checked(object sender, RoutedEventArgs e) => FilterApps();
        private void SortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => FilterApps();

        private void AppCheckBox_Click(object sender, RoutedEventArgs e)
        {
            UpdateBatchButton();
        }

        private void SelectAllCheckBox_Click(object sender, RoutedEventArgs e)
        {
            bool isChecked = SelectAllCheckBox.IsChecked == true;
            if (AppsList.ItemsSource is IEnumerable<InstalledApplication> visibleApps)
            {
                foreach (var app in visibleApps)
                {
                    app.IsSelected = isChecked;
                }
            }
            UpdateBatchButton();
        }

        private async void RefreshBtn_Click(object sender, RoutedEventArgs e)
        {
            HapticAudio.PlayClick();
            await LoadApplicationsAsync();
        }

        private async void Uninstall_Click(object sender, RoutedEventArgs e)
        {
            HapticAudio.PlayClick();
            if (sender is Button btn && btn.CommandParameter is InstalledApplication app)
            {
                await ExecuteStandardUninstall(app);
            }
        }

        private async Task ExecuteStandardUninstall(InstalledApplication app)
        {
            var res = MessageBox.Show(
                $"Запустить стандартное удаление программы:\n\n{app.DisplayName} (v{app.DisplayVersion})?",
                "Удаление программы", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (res != MessageBoxResult.Yes) return;

            StatusStatsText.Text = $"Удаление {app.DisplayName}...";
            try
            {
                bool success = await _removalService.RunStandardUninstallAsync(app);
                if (success)
                {
                    // Предложить очистить остатки
                    PromptForResiduals(app);
                    await LoadApplicationsAsync();
                }
                else
                {
                    MessageBox.Show("Деинсталлятор завершил работу с кодом, отличным от 0, либо был отменен пользователем.",
                        "Результат", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка при удалении: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                FilterApps();
            }
        }

        private async void QuietUninstall_Click(object sender, RoutedEventArgs e)
        {
            HapticAudio.PlayClick();
            if (sender is Button btn && btn.CommandParameter is InstalledApplication app)
            {
                var res = MessageBox.Show(
                    $"Выполнить ТИХОЕ удаление программы в фоновом режиме (без отображения диалогов установки):\n\n{app.DisplayName}?",
                    "Тихое удаление", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (res != MessageBoxResult.Yes) return;

                StatusStatsText.Text = $"Тихое удаление {app.DisplayName}...";
                try
                {
                    bool success = await _removalService.RunQuietUninstallAsync(app);
                    if (success)
                    {
                        MessageBox.Show($"Программа {app.DisplayName} успешно удалена в тихом режиме.", "Готово", MessageBoxButton.OK, MessageBoxImage.Information);
                        PromptForResiduals(app);
                        await LoadApplicationsAsync();
                    }
                    else
                    {
                        MessageBox.Show("Тихое удаление не удалось или требует интерактивного подтверждения. Попробуйте стандартное удаление.", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Ошибка: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    FilterApps();
                }
            }
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            HapticAudio.PlayClick();
            if (sender is Button btn && btn.CommandParameter is InstalledApplication app)
            {
                ApplicationRemovalService.OpenInstallLocation(app);
            }
        }

        private void MoreActionsBtn_Click(object sender, RoutedEventArgs e)
        {
            HapticAudio.PlayClick();
            if (sender is Button btn && btn.ContextMenu != null)
            {
                btn.ContextMenu.PlacementTarget = btn;
                btn.ContextMenu.DataContext = btn.CommandParameter;
                btn.ContextMenu.IsOpen = true;
            }
        }

        private async void ContextUninstall_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.DataContext is InstalledApplication app)
            {
                await ExecuteStandardUninstall(app);
            }
        }

        private async void ContextQuietUninstall_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.DataContext is InstalledApplication app)
            {
                StatusStatsText.Text = $"Тихое удаление {app.DisplayName}...";
                bool ok = await _removalService.RunQuietUninstallAsync(app);
                if (ok)
                {
                    PromptForResiduals(app);
                    await LoadApplicationsAsync();
                }
            }
        }

        private async void ContextForceUninstall_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.DataContext is InstalledApplication app)
            {
                var res = MessageBox.Show(
                    $"ВНИМАНИЕ! Принудительное удаление (Force Uninstall) удалит все записи реестра и папку программы для:\n\n{app.DisplayName}\n\nПродолжить?",
                    "Принудительное удаление", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (res != MessageBoxResult.Yes) return;

                StatusStatsText.Text = $"Принудительное удаление {app.DisplayName}...";
                bool ok = await _removalService.RunForceUninstallAsync(app, dryRun: false);
                if (ok)
                {
                    MessageBox.Show("Принудительное удаление завершено.", "Готово", MessageBoxButton.OK, MessageBoxImage.Information);
                    PromptForResiduals(app);
                    await LoadApplicationsAsync();
                }
                else
                {
                    MessageBox.Show("Не удалось удалить некоторые элементы.", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private async void ContextScanResiduals_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.DataContext is InstalledApplication app)
            {
                await ShowResidualsForApp(app);
            }
        }

        private void ContextOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.DataContext is InstalledApplication app)
            {
                ApplicationRemovalService.OpenInstallLocation(app);
            }
        }

        private void ContextOpenRegistry_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.DataContext is InstalledApplication app)
            {
                ApplicationRemovalService.OpenInRegistry(app);
            }
        }

        private void ContextSearchOnline_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.DataContext is InstalledApplication app)
            {
                ApplicationRemovalService.SearchOnline(app);
            }
        }

        private void ContextCopyInfo_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.DataContext is InstalledApplication app)
            {
                string info = $"Программа: {app.DisplayName}\nВерсия: {app.DisplayVersion}\nИздатель: {app.Publisher}\nДата установки: {app.InstallDateFormatted}\nРазмер: {app.EstimatedSize}\nПуть установки: {app.InstallLocation}\nРеестр: {app.RegistryKeyPath}\nКоманда удаления: {app.UninstallString}";
                try
                {
                    Clipboard.SetText(info);
                    MessageBox.Show("Сведения скопированы в буфер обмена.", "Копирование", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch { }
            }
        }

        private async void BatchUninstallBtn_Click(object sender, RoutedEventArgs e)
        {
            HapticAudio.PlayClick();
            var selected = _allApps.Where(a => a.IsSelected).ToList();
            if (selected.Count == 0) return;

            var res = MessageBox.Show(
                $"Выбрано для пакетного удаления программ: {selected.Count}.\n\nЗапустить последовательное удаление?",
                "Пакетное удаление", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (res != MessageBoxResult.Yes) return;

            int done = 0;
            foreach (var app in selected)
            {
                StatusStatsText.Text = $"Пакетное удаление ({++done}/{selected.Count}): {app.DisplayName}...";
                try
                {
                    await _removalService.RunStandardUninstallAsync(app);
                }
                catch { }
            }

            MessageBox.Show($"Пакетное удаление завершено. Обработано программ: {selected.Count}.", "Готово", MessageBoxButton.OK, MessageBoxImage.Information);
            await LoadApplicationsAsync();
        }

        private async void ScanLeftoversBtn_Click(object sender, RoutedEventArgs e)
        {
            HapticAudio.PlayClick();
            var orphaned = _allApps.Where(a => a.IsOrphaned).ToList();
            if (orphaned.Count > 0)
            {
                var app = orphaned.First();
                await ShowResidualsForApp(app);
            }
            else
            {
                var selectedApp = AppsList.SelectedItem as InstalledApplication ?? _allApps.FirstOrDefault();
                if (selectedApp != null)
                {
                    await ShowResidualsForApp(selectedApp);
                }
                else
                {
                    MessageBox.Show("Выберите программу в списке для поиска остаточных файлов.", "Поиск остатков", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }

        private async void PromptForResiduals(InstalledApplication app)
        {
            var res = MessageBox.Show(
                $"Желаете проверить систему на наличие остаточных файлов и записей реестра для «{app.DisplayName}»?",
                "Очистка остатков", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (res == MessageBoxResult.Yes)
            {
                await ShowResidualsForApp(app);
            }
        }

        private async Task ShowResidualsForApp(InstalledApplication app)
        {
            StatusStatsText.Text = $"Поиск остатков {app.DisplayName}...";
            var residuals = await _removalService.ScanResidualsAsync(app);
            _currentResiduals = residuals;

            if (residuals.Count == 0)
            {
                MessageBox.Show($"Остаточных файлов и пустых ключей реестра для «{app.DisplayName}» не обнаружено.", "Чисто", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ResidualsSubtitle.Text = $"Для «{app.DisplayName}» найдено остаточных объектов: {residuals.Count}";
            ResidualsList.ItemsSource = residuals;
            ResidualsOverlay.Visibility = Visibility.Visible;
        }

        private void CloseResiduals_Click(object sender, RoutedEventArgs e)
        {
            ResidualsOverlay.Visibility = Visibility.Collapsed;
        }

        private async void CleanResidualsConfirm_Click(object sender, RoutedEventArgs e)
        {
            if (_currentResiduals == null || _currentResiduals.Count == 0) return;

            int cleaned = await _removalService.CleanResidualsAsync(_currentResiduals);
            ResidualsOverlay.Visibility = Visibility.Collapsed;
            MessageBox.Show($"Успешно очищено остаточных элементов: {cleaned}.", "Очистка завершена", MessageBoxButton.OK, MessageBoxImage.Information);
            await LoadApplicationsAsync();
        }
    }
}
