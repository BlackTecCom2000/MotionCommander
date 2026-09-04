using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Win11CopyDialog.Helpers;
using Win11CopyDialog.Modules.FileManager.Models;
using Win11CopyDialog.Modules.FileManager.Services;

namespace Win11CopyDialog.Views.Dialogs;

public partial class CyberFolderPickerDialog : Window
{
    private string _currentPath = "";
    private readonly Stack<string> _backStack = new();
    private readonly Stack<string> _forwardStack = new();
    private readonly long? _requiredBytes;
    private List<FolderItemModel> _allFolders = new();

    public string SelectedPath { get; private set; } = "";

    public CyberFolderPickerDialog(string title = "ВЫБОР ПАПКИ НАЗНАЧЕНИЯ", string? initialPath = null, long? requiredBytes = null)
    {
        InitializeComponent();

        DialogTitleText.Text = title;
        _requiredBytes = requiredBytes;

        BackdropHelper.Apply(this, Models.ThemeManager.Instance.Backdrop, Models.ThemeManager.Instance.IsDark);

        // Загрузка дисков и быстрого доступа
        LoadDrivesAndQuickAccess();

        // Определение стартового пути
        string startPath = "";
        if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath))
        {
            startPath = initialPath;
        }
        else
        {
            startPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!Directory.Exists(startPath))
            {
                startPath = "C:\\";
            }
        }

        NavigateTo(startPath);
    }

    /// <summary>
    /// Статический вызов выбора папки в футуристическом стиле
    /// </summary>
    public static string? PickFolder(Window? owner, string title = "Выберите папку назначения", string? initialPath = null, long? requiredBytes = null)
    {
        var dlg = new CyberFolderPickerDialog(title, initialPath, requiredBytes);
        if (owner != null && owner.IsVisible)
        {
            dlg.Owner = owner;
        }
        bool? res = dlg.ShowDialog();
        return res == true ? dlg.SelectedPath : null;
    }

    private void LoadDrivesAndQuickAccess()
    {
        try
        {
            QuickAccessList.ItemsSource = FileSystemService.GetQuickAccessLocations();
            DriveChipsList.ItemsSource = FileSystemService.GetDrives();
        }
        catch { }
    }

    public void NavigateTo(string path, bool recordHistory = true)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;

        try
        {
            path = Path.GetFullPath(path);

            if (recordHistory && !string.IsNullOrEmpty(_currentPath) && !string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase))
            {
                _backStack.Push(_currentPath);
                _forwardStack.Clear();
            }

            _currentPath = path;
            SelectedPath = path;
            SelectedPathDisplay.Text = path;

            UpdateNavButtons();
            UpdateBreadcrumbs(path);
            UpdateDiskSpaceInfo(path);

            // Сканирование поддиректорий
            var dirInfo = new DirectoryInfo(path);
            var folders = new List<FolderItemModel>();

            try
            {
                foreach (var sub in dirInfo.EnumerateDirectories())
                {
                    try
                    {
                        if ((sub.Attributes & FileAttributes.Hidden) != 0 || (sub.Attributes & FileAttributes.System) != 0)
                            continue;

                        folders.Add(new FolderItemModel
                        {
                            FullPath = sub.FullName,
                            Name = sub.Name,
                            LastModified = sub.LastWriteTime.ToString("dd.MM.yyyy HH:mm"),
                            Subtitle = "Папка с файлами"
                        });
                    }
                    catch { }
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Защищенная системная папка
            }

            _allFolders = folders.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
            ApplyFilter();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось открыть папку: {ex.Message}", "Ошибка навигации", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ApplyFilter()
    {
        string filter = FilterBox?.Text?.Trim() ?? "";
        var filtered = string.IsNullOrEmpty(filter)
            ? _allFolders
            : _allFolders.Where(f => f.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        FoldersListView.ItemsSource = filtered;
        EmptyStatePanel.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateBreadcrumbs(string path)
    {
        var crumbs = new List<BreadcrumbItem>();
        try
        {
            var dir = new DirectoryInfo(path);
            var stack = new Stack<BreadcrumbItem>();

            while (dir != null)
            {
                string name = dir.Name;
                if (string.IsNullOrEmpty(name) || name == dir.FullName)
                {
                    name = dir.FullName.TrimEnd('\\', '/');
                }
                stack.Push(new BreadcrumbItem { Name = name, FullPath = dir.FullName });
                dir = dir.Parent;
            }

            crumbs.AddRange(stack);
        }
        catch { }

        BreadcrumbsList.ItemsSource = crumbs;
    }

    private void UpdateDiskSpaceInfo(string path)
    {
        try
        {
            string root = Path.GetPathRoot(path) ?? "";
            if (!string.IsNullOrEmpty(root))
            {
                var d = new DriveInfo(root);
                if (d.IsReady)
                {
                    DiskSpaceInfoText.Text = $"Свободно на диске {d.Name.TrimEnd('\\')}: {Formatters.Bytes(d.AvailableFreeSpace)} из {Formatters.Bytes(d.TotalSize)}";

                    if (_requiredBytes.HasValue && _requiredBytes.Value > 0)
                    {
                        long req = _requiredBytes.Value;
                        if (d.AvailableFreeSpace >= req)
                        {
                            RequiredSpaceText.Text = $"• Требуется: {Formatters.Bytes(req)} (Достаточно места ✔)";
                            RequiredSpaceText.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Green
                            ConfirmSelectBtn.IsEnabled = true;
                        }
                        else
                        {
                            RequiredSpaceText.Text = $"• Требуется: {Formatters.Bytes(req)} (Внимание: Недостаточно места! ⚠)";
                            RequiredSpaceText.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red
                        }
                    }
                    else
                    {
                        RequiredSpaceText.Text = "";
                    }
                }
            }
        }
        catch { }
    }

    private void UpdateNavButtons()
    {
        BackBtn.IsEnabled = _backStack.Count > 0;
        ForwardBtn.IsEnabled = _forwardStack.Count > 0;
        UpBtn.IsEnabled = Directory.GetParent(_currentPath) != null;
    }

    // ---------- СОБЫТИЯ НАВИГАЦИИ ----------

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_backStack.Count == 0) return;
        HapticAudio.PlayClick();
        string prev = _backStack.Pop();
        _forwardStack.Push(_currentPath);
        NavigateTo(prev, false);
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (_forwardStack.Count == 0) return;
        HapticAudio.PlayClick();
        string next = _forwardStack.Pop();
        _backStack.Push(_currentPath);
        NavigateTo(next, false);
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        var parent = Directory.GetParent(_currentPath);
        if (parent != null)
        {
            HapticAudio.PlayClick();
            NavigateTo(parent.FullName);
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        LoadDrivesAndQuickAccess();
        NavigateTo(_currentPath, false);
    }

    private void DriveChip_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DriveItem drive)
        {
            HapticAudio.PlayClick();
            NavigateTo(drive.RootDirectory);
        }
    }

    private void QuickAccessItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is QuickAccessItem item)
        {
            HapticAudio.PlayClick();
            NavigateTo(item.Path);
        }
    }

    private void Breadcrumb_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is BreadcrumbItem crumb && Directory.Exists(crumb.FullPath))
        {
            HapticAudio.PlayClick();
            NavigateTo(crumb.FullPath);
        }
    }

    private void FoldersListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FoldersListView.SelectedItem is FolderItemModel item && Directory.Exists(item.FullPath))
        {
            HapticAudio.PlayClick();
            NavigateTo(item.FullPath);
        }
    }

    private void FoldersListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FoldersListView.SelectedItem is FolderItemModel item && Directory.Exists(item.FullPath))
        {
            SelectedPath = item.FullPath;
            SelectedPathDisplay.Text = item.FullPath;
        }
        else
        {
            SelectedPath = _currentPath;
            SelectedPathDisplay.Text = _currentPath;
        }
    }

    private void FoldersListView_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (FoldersListView.SelectedItem is FolderItemModel item && Directory.Exists(item.FullPath))
            {
                NavigateTo(item.FullPath);
                e.Handled = true;
            }
            else
            {
                ConfirmSelect_Click(sender, e);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Back)
        {
            Up_Click(sender, e);
            e.Handled = true;
        }
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    // ---------- СОЗДАНИЕ ПАПКИ ----------

    private void NewFolderToggle_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        NewFolderBanner.Visibility = NewFolderBanner.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (NewFolderBanner.Visibility == Visibility.Visible)
        {
            NewFolderNameBox.Text = "Новая папка";
            NewFolderNameBox.Focus();
            NewFolderNameBox.SelectAll();
        }
    }

    private void CreateFolderConfirm_Click(object sender, RoutedEventArgs e)
    {
        string name = NewFolderNameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;

        try
        {
            string newPath = Path.Combine(_currentPath, name);
            if (!Directory.Exists(newPath))
            {
                Directory.CreateDirectory(newPath);
            }
            NewFolderBanner.Visibility = Visibility.Collapsed;
            HapticAudio.PlaySuccess();
            NavigateTo(newPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось создать папку: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CreateFolderCancel_Click(object sender, RoutedEventArgs e)
    {
        NewFolderBanner.Visibility = Visibility.Collapsed;
    }

    private void NewFolderNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CreateFolderConfirm_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CreateFolderCancel_Click(sender, e);
            e.Handled = true;
        }
    }

    private void CopySelectedPath_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(SelectedPath);
            HapticAudio.PlayClick();
        }
        catch { }
    }

    private void ConfirmSelect_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SelectedPath) || !Directory.Exists(SelectedPath))
        {
            SelectedPath = _currentPath;
        }

        if (Directory.Exists(SelectedPath))
        {
            HapticAudio.PlaySuccess();
            DialogResult = true;
            Close();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        DialogResult = false;
        Close();
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close_Click(sender, e);
            e.Handled = true;
        }
    }
}

public sealed class FolderItemModel
{
    public string FullPath { get; set; } = "";
    public string Name { get; set; } = "";
    public string LastModified { get; set; } = "";
    public string Subtitle { get; set; } = "";
}

public sealed class BreadcrumbItem
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
}
