using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Win11CopyDialog.Helpers;
using Win11CopyDialog.Models;
using Win11CopyDialog.Modules.UpdateEngine;

namespace Win11CopyDialog.Views.Dialogs;

public partial class VersionSelectDialog : Window
{
    private List<ReleaseVersionItem> _releases = new();
    private ReleaseVersionItem? _selectedItem;
    private CancellationTokenSource? _cts;

    public VersionSelectDialog()
    {
        InitializeComponent();
        ThemeManager.Instance.Apply();
        BackdropHelper.Apply(this, ThemeManager.Instance.Backdrop, ThemeManager.Instance.IsDark);

        string currentVer = UpdateService.GetCurrentVersion();
        CurrentVersionSubtext.Text = $"Текущая версия: v{currentVer} | Выберите любую версию для обновления или безопасного отката назад";
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadReleasesAsync();
    }

    private async Task LoadReleasesAsync()
    {
        LoadingPanel.Visibility = Visibility.Visible;
        ReleasesListBox.Visibility = Visibility.Collapsed;
        RefreshReleasesBtn.IsEnabled = false;
        BottomStatusText.Text = "Загрузка истории версий из репозитория...";

        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        try
        {
            _releases = await UpdateService.GetAvailableReleasesAsync(_cts.Token);
            ReleasesListBox.ItemsSource = _releases;

            LoadingPanel.Visibility = Visibility.Collapsed;
            ReleasesListBox.Visibility = Visibility.Visible;
            RefreshReleasesBtn.IsEnabled = true;

            string currentVer = UpdateService.GetCurrentVersion();
            BottomStatusText.Text = $"Загружено версий: {_releases.Count} (текущая установлена: v{currentVer})";

            // Выбираем по умолчанию: либо первую доступную для обновления, либо текущую версию
            int selectIdx = _releases.FindIndex(r => r.IsUpgrade);
            if (selectIdx < 0)
            {
                selectIdx = _releases.FindIndex(r => r.IsCurrent);
            }
            if (selectIdx < 0 && _releases.Count > 0)
            {
                selectIdx = 0;
            }

            if (selectIdx >= 0)
            {
                ReleasesListBox.SelectedIndex = selectIdx;
            }
        }
        catch (Exception ex)
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
            RefreshReleasesBtn.IsEnabled = true;
            BottomStatusText.Text = $"Ошибка загрузки: {ex.Message}";
            ChangelogTextBox.Text = $"Не удалось загрузить список релизов:\n{ex.Message}";
        }
    }

    private void ReleasesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedItem = ReleasesListBox.SelectedItem as ReleaseVersionItem;
        if (_selectedItem == null)
        {
            ApplyVersionBtn.IsEnabled = false;
            return;
        }

        SelectedVersionTitle.Text = $"Motion Commander v{_selectedItem.Version}";
        SelectedVersionDate.Text = !string.IsNullOrEmpty(_selectedItem.ReleaseDate)
            ? $"Дата релиза: {_selectedItem.ReleaseDate}"
            : "Официальный дистрибутив Motion Commander";

        // Чейнджлог
        ChangelogTextBox.Text = !string.IsNullOrWhiteSpace(_selectedItem.Body)
            ? _selectedItem.Body
            : $"Релиз версии v{_selectedItem.Version}.\nПоддерживает автономную установку и бесшовное обновление.";

        // Бейдж и кнопки в зависимости от типа версии (Текущая / Обновление / Откат)
        if (_selectedItem.IsCurrent)
        {
            SelectedStatusBadgeText.Text = "Текущая запущенная версия";
            RollbackWarningCard.Visibility = Visibility.Collapsed;
            ApplyVersionBtn.Content = "↺ Переустановить текущую версию";
            ApplyVersionBtn.IsEnabled = true;
            BottomStatusText.Text = $"Выбрана текущая версия v{_selectedItem.Version}";
        }
        else if (_selectedItem.IsUpgrade)
        {
            SelectedStatusBadgeText.Text = "🚀 Доступно обновление";
            RollbackWarningCard.Visibility = Visibility.Collapsed;
            ApplyVersionBtn.Content = $"🚀 Обновить до v{_selectedItem.Version}";
            ApplyVersionBtn.IsEnabled = true;
            BottomStatusText.Text = $"Обновление с v{UpdateService.GetCurrentVersion()} до v{_selectedItem.Version}";
        }
        else
        {
            SelectedStatusBadgeText.Text = "↺ Режим отката (Rollback)";
            RollbackWarningCard.Visibility = Visibility.Visible;
            ApplyVersionBtn.Content = $"↺ Откатить на v{_selectedItem.Version}";
            ApplyVersionBtn.IsEnabled = true;
            BottomStatusText.Text = $"Внимание: откат с v{UpdateService.GetCurrentVersion()} назад на v{_selectedItem.Version}";
        }
    }

    private void ApplyVersionBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem == null) return;

        HapticAudio.PlayClick();

        string currentVer = UpdateService.GetCurrentVersion();
        string actionVerb = _selectedItem.IsUpgrade ? "обновить" : (_selectedItem.IsDowngrade ? "откатить назад" : "переустановить");

        var confirm = MessageBox.Show(
            $"Вы действительно хотите {actionVerb} Motion Commander до версии v{_selectedItem.Version}?\n\n" +
            $"Текущая версия: v{currentVer}\n" +
            $"Целевая версия: v{_selectedItem.Version}\n\n" +
            "Приложение автоматически скачает необходимые файлы и бесшовно перезапустится.",
            $"Подтверждение: {actionVerb.ToUpperInvariant()} до v{_selectedItem.Version}",
            MessageBoxButton.YesNo,
            _selectedItem.IsDowngrade ? MessageBoxImage.Warning : MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        var switchMode = _selectedItem.IsUpgrade
            ? VersionSwitchMode.Upgrade
            : (_selectedItem.IsDowngrade ? VersionSwitchMode.Downgrade : VersionSwitchMode.Reinstall);

        var updateInfo = new UpdateInfo
        {
            CurrentVersion = currentVer,
            LatestVersion = _selectedItem.Version,
            IsUpdateAvailable = true,
            DownloadUrl = _selectedItem.DownloadUrl,
            InstallerUrl = _selectedItem.DownloadUrl,
            SetupExeUrl = _selectedItem.SetupExeUrl,
            ReleaseDate = _selectedItem.ReleaseDate,
            SwitchMode = switchMode
        };

        var progressWin = new UpdateProgressWindow(updateInfo)
        {
            Owner = this
        };

        this.Hide();
        progressWin.ShowDialog();
        this.Close();
    }

    private async void RefreshReleasesBtn_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        await LoadReleasesAsync();
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        Close();
    }
}
