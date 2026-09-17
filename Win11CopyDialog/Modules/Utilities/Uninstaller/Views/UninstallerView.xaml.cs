using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Win11CopyDialog.Modules.Utilities.Uninstaller.Models;
using Win11CopyDialog.Modules.Utilities.Uninstaller.Services;

namespace Win11CopyDialog.Modules.Utilities.Uninstaller.Views
{
    public partial class UninstallerView : UserControl
    {
        private readonly ApplicationDiscoveryService _discoveryService;
        private readonly ApplicationRemovalService _removalService;
        private List<InstalledApplication> _allApps;

        public UninstallerView()
        {
            InitializeComponent();
            
            // Add boolean converter locally if not in global resources
            if (!Resources.Contains("BooleanToVisibilityConverter"))
            {
                Resources.Add("BooleanToVisibilityConverter", new BooleanToVisibilityConverter());
            }

            _discoveryService = new ApplicationDiscoveryService();
            _removalService = new ApplicationRemovalService();
            _allApps = new List<InstalledApplication>();

            Loaded += UninstallerView_Loaded;
        }

        private async void UninstallerView_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadApplicationsAsync();
        }

        private async System.Threading.Tasks.Task LoadApplicationsAsync()
        {
            StatusText.Text = "Сбор информации о приложениях...";
            RefreshBtn.IsEnabled = false;

            try
            {
                _allApps = await System.Threading.Tasks.Task.Run(() => _discoveryService.GetInstalledApplications());
                FilterApps();
                StatusText.Text = $"Найдено программ: {_allApps.Count} (Скрыто системных компонентов)";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Ошибка при загрузке: {ex.Message}";
            }
            finally
            {
                RefreshBtn.IsEnabled = true;
            }
        }

        private void FilterApps()
        {
            var query = SearchBox.Text.ToLower();
            var filtered = _allApps.Where(a => 
                (string.IsNullOrEmpty(query) || a.DisplayName.ToLower().Contains(query) || a.Publisher.ToLower().Contains(query))
                // Optionally hide system components by default unless searched
            ).ToList();

            AppsList.ItemsSource = filtered;
        }

        private async void RefreshBtn_Click(object sender, RoutedEventArgs e)
        {
            await LoadApplicationsAsync();
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            FilterApps();
        }

        private void AppsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Update details panel if needed
        }

        private async void Uninstall_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is InstalledApplication app)
            {
                var result = MessageBox.Show($"Вы действительно хотите запустить стандартное удаление для:\n{app.DisplayName}?", 
                                             "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    StatusText.Text = $"Удаление {app.DisplayName}...";
                    bool success = await _removalService.RunStandardUninstallAsync(app);
                    if (success)
                    {
                        MessageBox.Show("Удаление завершено.", "Информация", MessageBoxButton.OK, MessageBoxImage.Information);
                        await LoadApplicationsAsync();
                    }
                    else
                    {
                        MessageBox.Show("Программа удаления вернула ошибку или была отменена.", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
                        StatusText.Text = "Ожидание...";
                    }
                }
            }
        }

        private async void ForceUninstall_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is InstalledApplication app)
            {
                var result = MessageBox.Show($"ВНИМАНИЕ!\nПринудительное удаление (Force Uninstall) удалит записи реестра и папку программы для:\n{app.DisplayName}\n\nРекомендуется использовать только если стандартное удаление не работает. Продолжить?", 
                                             "Принудительное удаление", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes)
                {
                    // Execute dry-run first
                    await _removalService.RunForceUninstallAsync(app, dryRun: true);

                    var finalResult = MessageBox.Show($"План принудительного удаления:\n- Удаление реестра: {app.RegistryKeyPath}\n- Удаление папки: {app.InstallLocation}\n\nВыполнить?", 
                                             "Подтверждение плана", MessageBoxButton.YesNo, MessageBoxImage.Error);

                    if (finalResult == MessageBoxResult.Yes)
                    {
                        StatusText.Text = $"Принудительное удаление {app.DisplayName}...";
                        bool success = await _removalService.RunForceUninstallAsync(app, dryRun: false);
                        if (success)
                        {
                            MessageBox.Show("Принудительное удаление завершено.", "Информация", MessageBoxButton.OK, MessageBoxImage.Information);
                            await LoadApplicationsAsync();
                        }
                        else
                        {
                            MessageBox.Show("Произошла ошибка при принудительном удалении. Возможно, требуются права администратора или файлы заняты.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                            StatusText.Text = "Ожидание...";
                        }
                    }
                }
            }
        }
    }
}
