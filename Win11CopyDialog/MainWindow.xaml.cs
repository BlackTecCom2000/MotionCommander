using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Win11CopyDialog.Helpers;
using Win11CopyDialog.Models;
using Win11CopyDialog.Modules.AdvancedTools.ChecksumEngine;
using Win11CopyDialog.Modules.ArchiveEngine.Services;
using Win11CopyDialog.Modules.FileManager.Models;
using Win11CopyDialog.Modules.FileManager.Services;
using Win11CopyDialog.Modules.WindowsShellIntegration;
using Win11CopyDialog.Modules.PerformanceEngine;
using Win11CopyDialog.Modules.UpdateEngine;
using Win11CopyDialog.Views.Dialogs;
using System.Windows.Media;

namespace Win11CopyDialog;

public partial class MainWindow : Window
{
    private string _currentPath = "";
    private bool _isInsideArchive;
    private string _currentArchivePath = "";
    private bool _showHidden;
    private string _searchFilter = "";

    private readonly Stack<string> _backStack = new();
    private readonly Stack<string> _forwardStack = new();
    private readonly List<string> _clipboardPaths = new();
    private bool _clipboardIsCut;

    private readonly string? _initialPath;
    private readonly int _initialTab;
    private bool _isInitialized;

    // Поля телеметрии и конвейера скорости
    private readonly Queue<double> _speedSamples = new();
    private System.Windows.Threading.DispatcherTimer? _speedGraphTimer;
    private double _currentSpeedMb;
    private double _peakSpeedMb;
    private CancellationTokenSource? _transferCts;
    private bool _transferIsPaused;

    public MainWindow(string? initialPath = null, int initialTab = 0)
    {
        _isInitialized = false;
        _initialPath = initialPath;
        _initialTab = initialTab;
        InitializeComponent();
        _isInitialized = true;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ThemeManager.Instance.Apply();
        BackdropHelper.Apply(this, ThemeManager.Instance.Backdrop, ThemeManager.Instance.IsDark);

        // Загрузка дисков и быстрого доступа
        RefreshDrivesAndQuickAccess();

        // Стартовая директория: переданный путь -> Документы -> UserProfile
        string startPath = _initialPath ?? "";
        if (string.IsNullOrEmpty(startPath) || !Directory.Exists(startPath))
        {
            startPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(startPath) || !Directory.Exists(startPath))
                startPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        NavigateTo(startPath);


        // Запуск 30 FPS телеметрии графика скорости
        InitSpeedGraph();

        string currentVer = UpdateService.GetCurrentVersion();
        if (AppVersionHeaderBadgeText != null) AppVersionHeaderBadgeText.Text = $"v{currentVer} All-in-One";

        if (_initialTab > 0)
        {
            SelectTab(_initialTab);
        }

        CheckForUpdatesOnStartupAsync();
    }

    private async void CheckForUpdatesOnStartupAsync()
    {
        try
        {
            var updateInfo = await UpdateService.CheckForUpdatesAsync();
            if (updateInfo.IsUpdateAvailable)
            {
                AvailableUpdateBtn.Visibility = Visibility.Visible;
                AvailableUpdateBtn.ToolTip = $"Доступна новая версия: v{updateInfo.LatestVersion}";
            }
        }
        catch { }
    }

    private async void AvailableUpdateBtn_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        AvailableUpdateBtn.IsEnabled = false;
        
        try
        {
            var updateInfo = await UpdateService.CheckForUpdatesAsync();
            if (updateInfo.IsUpdateAvailable)
            {
                var res = MessageBox.Show(
                    $"Доступна новая версия: v{updateInfo.LatestVersion}!\nТекущая версия: v{updateInfo.CurrentVersion}\n\nСкачать и установить обновление?", 
                    "Доступно обновление", 
                    MessageBoxButton.YesNo, 
                    MessageBoxImage.Information);

                if (res == MessageBoxResult.Yes)
                {
                    string downloadedFile = await UpdateService.DownloadUpdateAsync(
                        !string.IsNullOrEmpty(updateInfo.InstallerUrl) ? updateInfo.InstallerUrl : updateInfo.DownloadUrl);
                    UpdateService.ApplyUpdateAndRestart(downloadedFile);
                }
                else
                {
                    AvailableUpdateBtn.IsEnabled = true;
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка обновления:\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            AvailableUpdateBtn.IsEnabled = true;
        }
    }

    public void SelectTab(int index)
    {
        RadioButton target = index switch
        {
            1 => TabTransferRadio,
            2 => TabStorageRadio,
            3 => TabDiagnosticsRadio,
            4 => TabToolsRadio,
            _ => TabFilesRadio
        };
        SwitchTab(target);
    }

    public void ScrollSettingsToBottom()
    {
    }


    private void RefreshDrivesAndQuickAccess()
    {
        var quickItems = FileSystemService.GetQuickAccessLocations();
        QuickAccessList.ItemsSource = quickItems;
        if (SidebarQuickAccessCountBadge != null)
            SidebarQuickAccessCountBadge.Text = quickItems.Count.ToString();

        var drives = FileSystemService.GetDrives();
        DrivesList.ItemsSource = drives;
        if (SidebarDrivesCountBadge != null)
            SidebarDrivesCountBadge.Text = $"{drives.Count} тома";

        if (SidebarStorageSummaryText != null)
        {
            long totalBytes = drives.Sum(d => d.TotalSize);
            long freeBytes = drives.Sum(d => d.FreeSpace);
            SidebarStorageSummaryText.Text = totalBytes > 0
                ? $"{Formatters.Bytes(freeBytes)} свободно из {Formatters.Bytes(totalBytes)}"
                : "Накопители готовы";
        }
    }

    // ---------- НАВИГАЦИЯ ----------

    public void NavigateTo(string path, bool recordHistory = true)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        if (ArchiveService.IsArchive(path) && File.Exists(path))
        {
            OpenArchiveVirtual(path, recordHistory);
            return;
        }

        if (!Directory.Exists(path)) return;

        if (recordHistory && !string.IsNullOrEmpty(_currentPath) && _currentPath != path)
        {
            _backStack.Push(_isInsideArchive ? _currentArchivePath : _currentPath);
            _forwardStack.Clear();
        }

        _isInsideArchive = false;
        _currentArchivePath = "";
        _currentPath = Path.GetFullPath(path);
        PathBox.Text = _currentPath;

        var items = FileSystemService.EnumeratePath(_currentPath, _showHidden, _searchFilter);
        FileBrowserList.ItemsSource = items;

        UpdateNavButtons();
        UpdateStatusBar(items);
    }

    private void OpenArchiveVirtual(string archivePath, bool recordHistory = true)
    {
        try
        {
            var entries = ArchiveService.ReadArchiveEntries(archivePath);
            if (recordHistory && !string.IsNullOrEmpty(_currentPath))
            {
                _backStack.Push(_currentPath);
                _forwardStack.Clear();
            }

            _isInsideArchive = true;
            _currentArchivePath = archivePath;
            PathBox.Text = $"🗜 {archivePath}";
            FileBrowserList.ItemsSource = entries;

            UpdateNavButtons();
            StatusFilesText.Text = $"Архив: {Path.GetFileName(archivePath)} • {entries.Count} файлов";
            StatusDiskText.Text = "Виртуальный просмотр (без распаковки)";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось открыть архив: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateNavButtons()
    {
        BackBtn.IsEnabled = _backStack.Count > 0;
        ForwardBtn.IsEnabled = _forwardStack.Count > 0;
        UpBtn.IsEnabled = !_isInsideArchive && Directory.GetParent(_currentPath) != null;
    }

    private void UpdateStatusBar(IReadOnlyList<FileSystemItem> items)
    {
        long totalSize = items.Where(i => !i.IsDirectory).Sum(i => i.Length);
        StatusFilesText.Text = $"{items.Count} элементов (файлы: {Formatters.Bytes(totalSize)})";

        try
        {
            string root = Path.GetPathRoot(_currentPath) ?? "";
            if (!string.IsNullOrEmpty(root))
            {
                var d = new DriveInfo(root);
                if (d.IsReady)
                {
                    StatusDiskText.Text = $"Свободно на диске {d.Name}: {Formatters.Bytes(d.AvailableFreeSpace)} из {Formatters.Bytes(d.TotalSize)}";
                }
            }
        }
        catch { }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_backStack.Count == 0) return;
        HapticAudio.PlayClick();
        string prev = _backStack.Pop();
        _forwardStack.Push(_isInsideArchive ? _currentArchivePath : _currentPath);
        NavigateTo(prev, false);
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (_forwardStack.Count == 0) return;
        HapticAudio.PlayClick();
        string next = _forwardStack.Pop();
        _backStack.Push(_isInsideArchive ? _currentArchivePath : _currentPath);
        NavigateTo(next, false);
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_isInsideArchive)
        {
            string dir = Path.GetDirectoryName(_currentArchivePath) ?? "";
            NavigateTo(dir);
            return;
        }

        var parent = Directory.GetParent(_currentPath);
        if (parent != null)
        {
            HapticAudio.PlayClick();
            NavigateTo(parent.FullName);
        }
    }

    private void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            string p = PathBox.Text.Trim();
            if (Directory.Exists(p) || File.Exists(p))
            {
                NavigateTo(p);
            }
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchFilter = SearchBox.Text.Trim();
        if (ClearSearchBtn != null)
        {
            ClearSearchBtn.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Collapsed : Visibility.Visible;
        }
        if (!_isInsideArchive && Directory.Exists(_currentPath))
        {
            FileBrowserList.ItemsSource = FileSystemService.EnumeratePath(_currentPath, _showHidden, _searchFilter);
        }
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        SearchBox.Text = string.Empty;
        SearchBox.Focus();
    }

    private void CopyCurrentPath_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrEmpty(PathBox.Text))
            {
                Clipboard.SetText(PathBox.Text);
                HapticAudio.PlaySuccess();
            }
        }
        catch { }
    }

    private void QuickAccess_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is QuickAccessItem item)
        {
            HapticAudio.PlayClick();
            NavigateTo(item.Path);
        }
    }

    private void Drive_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DriveItem drive)
        {
            HapticAudio.PlayClick();
            NavigateTo(drive.RootDirectory);
        }
    }

    private void RefreshDrives_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        RefreshDrivesAndQuickAccess();
    }

    private void DriveContextOpen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is DriveItem drive)
        {
            HapticAudio.PlayClick();
            NavigateTo(drive.RootDirectory);
        }
    }

    private void DriveContextWizTree_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is DriveItem drive)
        {
            HapticAudio.PlayClick();
            new Views.Dialogs.WizTreeAnalyzerWindow(drive.RootDirectory) { Owner = this }.Show();
        }
    }

    private void DriveContextSpeed_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is DriveItem drive)
        {
            HapticAudio.PlayClick();
            SelectTab(1);
        }
    }

    private void DriveContextProperties_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is DriveItem drive)
        {
            HapticAudio.PlayClick();
            try
            {
                string info = $"Метка тома: {drive.Title}\n" +
                              $"Буква накопителя: {drive.CleanDriveLetter}\n" +
                              $"Файловая система: {drive.DriveBadge}\n" +
                              $"Тип устройства: {drive.Subtitle}\n" +
                              $"Общая емкость: {Formatters.Bytes(drive.TotalSize)}\n" +
                              $"Свободное пространство: {drive.FreeSpaceFormatted} ({100.0 - drive.PercentUsed:F1}%)\n" +
                              $"Занято данными: {drive.UsedSpaceFormatted} ({drive.PercentFormatted})";
                MessageBox.Show(info, $"Свойства накопителя {drive.CleanDriveLetter}", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Свойства диска", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void QuickAccessContextOpen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is QuickAccessItem item)
        {
            HapticAudio.PlayClick();
            NavigateTo(item.Path);
        }
    }

    private void QuickAccessContextTerminal_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is QuickAccessItem item && Directory.Exists(item.Path))
        {
            HapticAudio.PlayClick();
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    WorkingDirectory = item.Path,
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }

    private void QuickAccessContextCopyPath_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is QuickAccessItem item)
        {
            HapticAudio.PlayClick();
            try { Clipboard.SetText(item.Path); } catch { }
        }
    }

    private void ShowHidden_Click(object sender, RoutedEventArgs e)
    {
        _showHidden = ShowHiddenCheck.IsChecked == true;
        if (!_isInsideArchive) NavigateTo(_currentPath, false);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        RefreshDrivesAndQuickAccess();
        if (_isInsideArchive) OpenArchiveVirtual(_currentArchivePath, false);
        else NavigateTo(_currentPath, false);
    }

    private void FileBrowserList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileBrowserList.SelectedItem is FileSystemItem item)
        {
            HapticAudio.PlayClick();
            if (_isInsideArchive)
            {
                // Внутри архива: распаковать и открыть выбранный файл
                return;
            }

            if (item.IsDirectory)
            {
                NavigateTo(item.FullPath);
            }
            else if (item.IsArchive)
            {
                OpenArchiveVirtual(item.FullPath);
            }
            else
            {
                try
                {
                    Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true });
                }
                catch { }
            }
        }
    }

    private void FileBrowserList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = FileBrowserList.SelectedItems.Cast<FileSystemItem>().ToList();
        if (selected.Count > 0)
        {
            long sz = selected.Where(s => !s.IsDirectory).Sum(s => s.Length);
            StatusFilesText.Text = $"Выбрано: {selected.Count} элементов ({Formatters.Bytes(sz)})";
        }
    }

    // ---------- КОМАНДЫ АРХИВАТОРА ----------

    private void CreateArchive_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        var selected = FileBrowserList.SelectedItems.Cast<FileSystemItem>().Select(i => i.FullPath).ToList();
        if (selected.Count == 0 && !_isInsideArchive)
        {
            selected.Add(_currentPath);
        }

        var dlg = new CreateArchiveWindow(selected) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            Refresh_Click(this, new RoutedEventArgs());
        }
    }

    private void ExtractArchive_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        string targetArchive = "";
        if (_isInsideArchive)
        {
            targetArchive = _currentArchivePath;
        }
        else
        {
            var item = FileBrowserList.SelectedItem as FileSystemItem;
            if (item != null && item.IsArchive) targetArchive = item.FullPath;
        }

        if (string.IsNullOrEmpty(targetArchive))
        {
            MessageBox.Show("Выберите архив для распаковки.", "Внимание", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new ExtractArchiveWindow(targetArchive) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            Refresh_Click(this, new RoutedEventArgs());
        }
    }

    private void TestArchive_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        string arc = _isInsideArchive ? _currentArchivePath : (FileBrowserList.SelectedItem as FileSystemItem)?.FullPath ?? "";
        if (string.IsNullOrEmpty(arc) || !ArchiveService.IsArchive(arc))
        {
            MessageBox.Show("Выберите архив для проверки.", "Внимание", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new ExtractArchiveWindow(arc) { Owner = this };
        dlg.ShowDialog();
    }

    // ---------- ФАЙЛОВЫЕ ОПЕРАЦИИ ----------

    private void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        if (_isInsideArchive) return;

        if (FileSystemService.CreateFolder(_currentPath, "Новая папка", out _, out string err))
        {
            NavigateTo(_currentPath, false);
        }
        else
        {
            MessageBox.Show(err, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DeleteItem_Click(object sender, RoutedEventArgs e)
    {
        var items = FileBrowserList.SelectedItems.Cast<FileSystemItem>().ToList();
        if (items.Count == 0 || _isInsideArchive) return;

        HapticAudio.PlayClick();
        if (MessageBox.Show($"Удалить {items.Count} элементов?", "Подтверждение удаления", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            foreach (var item in items)
            {
                FileSystemService.DeleteItem(item.FullPath, false, out _);
            }
            NavigateTo(_currentPath, false);
        }
    }

    private void CopyItem_Click(object sender, RoutedEventArgs e)
    {
        var items = FileBrowserList.SelectedItems.Cast<FileSystemItem>().ToList();
        if (items.Count == 0) return;
        HapticAudio.PlayClick();
        _clipboardPaths.Clear();
        _clipboardPaths.AddRange(items.Select(i => i.FullPath));
        _clipboardIsCut = false;
    }

    private void CutItem_Click(object sender, RoutedEventArgs e)
    {
        var items = FileBrowserList.SelectedItems.Cast<FileSystemItem>().ToList();
        if (items.Count == 0) return;
        HapticAudio.PlayClick();
        _clipboardPaths.Clear();
        _clipboardPaths.AddRange(items.Select(i => i.FullPath));
        _clipboardIsCut = true;
    }

    private async void PasteItem_Click(object sender, RoutedEventArgs e)
    {
        if (_clipboardPaths.Count == 0 || _isInsideArchive) return;
        HapticAudio.PlayClick();

        var sources = _clipboardPaths.ToList();
        bool isCut = _clipboardIsCut;

        var motion = new MotionCopyWindow { Owner = this };
        motion.Show();
        motion.Activate();

        try
        {
            await motion.StartRealTransferAsync(sources, _currentPath);

            if (isCut && motion.Engine.IsCompleted)
            {
                foreach (var s in sources)
                {
                    try
                    {
                        if (File.Exists(s)) File.Delete(s);
                        else if (Directory.Exists(s)) Directory.Delete(s, true);
                    }
                    catch { }
                }
                _clipboardPaths.Clear();
                _clipboardIsCut = false;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка вставки: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        NavigateTo(_currentPath, false);
    }

    private async void CopyToFolder_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        var items = FileBrowserList.SelectedItems.Cast<FileSystemItem>().ToList();
        if (items.Count == 0)
        {
            MessageBox.Show("Выберите один или несколько файлов или папок для копирования.", "Копирование в папку", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        long totalBytes = 0;
        foreach (var it in items)
        {
            if (!it.IsDirectory)
                totalBytes += it.Length;
        }

        string? targetDir = Win11CopyDialog.Views.Dialogs.CyberFolderPickerDialog.PickFolder(
            this,
            $"Выберите целевую папку для копирования ({items.Count} эл.)",
            _currentPath,
            totalBytes > 0 ? totalBytes : null);

        if (string.IsNullOrEmpty(targetDir) || !Directory.Exists(targetDir)) return;

        var sources = items.Select(i => i.FullPath).ToList();
        await LaunchRealTransferBatchAsync(sources, targetDir, isCut: false);
    }

    private async void MoveToFolder_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        var items = FileBrowserList.SelectedItems.Cast<FileSystemItem>().ToList();
        if (items.Count == 0)
        {
            MessageBox.Show("Выберите один или несколько файлов или папок для перемещения.", "Перемещение в папку", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        long totalBytes = 0;
        foreach (var it in items)
        {
            if (!it.IsDirectory)
                totalBytes += it.Length;
        }

        string? targetDir = Win11CopyDialog.Views.Dialogs.CyberFolderPickerDialog.PickFolder(
            this,
            $"Выберите целевую папку для перемещения ({items.Count} эл.)",
            _currentPath,
            totalBytes > 0 ? totalBytes : null);

        if (string.IsNullOrEmpty(targetDir) || !Directory.Exists(targetDir)) return;

        var sources = items.Select(i => i.FullPath).ToList();
        await LaunchRealTransferBatchAsync(sources, targetDir, isCut: true);
    }

    private async Task LaunchRealTransferBatchAsync(IEnumerable<string> sources, string dst, bool isCut = false)
    {
        var sList = sources.ToList();
        if (sList.Count == 0) return;

        var motion = new MotionCopyWindow { Owner = this };
        _activeMotionWindow = motion;
        motion.Show();
        motion.Activate();

        try
        {
            await motion.StartRealTransferAsync(sList, dst);

            if (isCut && motion.Engine.IsCompleted)
            {
                foreach (var s in sList)
                {
                    try
                    {
                        if (File.Exists(s)) File.Delete(s);
                        else if (Directory.Exists(s)) Directory.Delete(s, true);
                    }
                    catch { }
                }
            }
            HapticAudio.PlaySuccess();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка передачи данных: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        NavigateTo(_currentPath, false);
    }

    private void ShowHashes_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        var item = FileBrowserList.SelectedItem as FileSystemItem;
        var dlg = new AdvancedToolsWindow(item?.FullPath ?? _currentPath) { Owner = this };
        dlg.ShowDialog();
    }

    // ---------- КОНТЕКСТНОЕ МЕНЮ И КЛАВИАТУРА ----------

    private void FileBrowserList_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.C)
            {
                CopyItem_Click(sender, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Key.X)
            {
                CutItem_Click(sender, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Key.V)
            {
                PasteItem_Click(sender, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Key.A)
            {
                FileBrowserList.SelectAll();
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Delete)
        {
            DeleteItem_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.F2)
        {
            ContextRename_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            Refresh_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void ContextOpen_Click(object sender, RoutedEventArgs e) => FileBrowserList_DoubleClick(this, null!);
    private void ContextCompress_Click(object sender, RoutedEventArgs e) => CreateArchive_Click(this, e);
    private void ContextExtract_Click(object sender, RoutedEventArgs e) => ExtractArchive_Click(this, e);
    private void ContextHashes_Click(object sender, RoutedEventArgs e) => ShowHashes_Click(this, e);

    private void ContextDuplicate_Click(object sender, RoutedEventArgs e)
    {
        if (FileBrowserList.SelectedItem is FileSystemItem item && !_isInsideArchive)
        {
            HapticAudio.PlayClick();
            FileSystemService.Duplicate(item.FullPath, out _, out _);
            NavigateTo(_currentPath, false);
        }
    }

    private void ContextRename_Click(object sender, RoutedEventArgs e)
    {
        if (FileBrowserList.SelectedItem is FileSystemItem item && !_isInsideArchive)
        {
            HapticAudio.PlayClick();
            
            var window = new Window
            {
                Title = "Переименовать",
                Width = 400,
                Height = 160,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = (Brush)FindResource("WindowBackgroundBrush"),
                Foreground = (Brush)FindResource("PrimaryTextBrush")
            };
            var stack = new StackPanel { Margin = new Thickness(20) };
            stack.Children.Add(new TextBlock { Text = "Введите новое имя:", Margin = new Thickness(0,0,0,10), FontSize = 14, FontWeight = FontWeights.SemiBold });
            var textBox = new TextBox { Text = item.Name, Padding = new Thickness(6), FontSize = 14, Background = (Brush)FindResource("ControlBackgroundBrush"), Foreground = (Brush)FindResource("PrimaryTextBrush") };
            stack.Children.Add(textBox);
            var btnStack = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,15,0,0) };
            
            var btnCancel = new Button { Content = "Отмена", Width = 90, Padding = new Thickness(5), Margin = new Thickness(0,0,10,0), Style = (Style)FindResource("CyberToolButton") };
            btnCancel.Click += (s, args) => { window.DialogResult = false; window.Close(); };
            
            var btnOk = new Button { Content = "OK", Width = 90, Padding = new Thickness(5), Style = (Style)FindResource("CyberPrimaryButton") };
            btnOk.Click += (s, args) => { window.DialogResult = true; window.Close(); };
            
            btnStack.Children.Add(btnCancel);
            btnStack.Children.Add(btnOk);
            stack.Children.Add(btnStack);
            window.Content = stack;
            
            textBox.SelectAll();
            textBox.Focus();
            
            if (window.ShowDialog() == true && !string.IsNullOrWhiteSpace(textBox.Text) && textBox.Text != item.Name)
            {
                FileSystemService.Rename(item.FullPath, textBox.Text, out _, out var error);
                if (!string.IsNullOrEmpty(error))
                {
                    MessageBox.Show(error, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                NavigateTo(_currentPath, false);
            }
        }
    }

    private void ContextProperties_Click(object sender, RoutedEventArgs e)
    {
        if (FileBrowserList.SelectedItem is FileSystemItem item)
        {
            HapticAudio.PlayClick();
            MessageBox.Show($"Имя: {item.Name}\nПуть: {item.FullPath}\nРазмер: {item.SizeFormatted}\nИзменен: {item.DateModifiedFormatted}\nCRC32: {item.CrcHex}", "Свойства", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void FileBrowserList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var originalSource = e.OriginalSource as DependencyObject;
        while (originalSource != null && !(originalSource is ListViewItem))
        {
            originalSource = VisualTreeHelper.GetParent(originalSource);
        }

        if (originalSource is ListViewItem item && item.DataContext is FileSystemItem fsItem)
        {
            if (!FileBrowserList.SelectedItems.Contains(fsItem))
            {
                FileBrowserList.SelectedItem = fsItem;
            }
        }
    }

    private async void FileBrowserList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && !_isInsideArchive)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                HapticAudio.PlayClick();
                var motion = new MotionCopyWindow { Owner = this };
                motion.Show();
                motion.Activate();

                try
                {
                    await motion.StartRealTransferAsync(files, _currentPath);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Ошибка копирования: {ex.Message}", "Ошибка I/O", MessageBoxButton.OK, MessageBoxImage.Error);
                }

                NavigateTo(_currentPath, false);
            }
        }
    }

    // ---------- ПЕРЕКЛЮЧЕНИЕ ВКЛАДОК ----------

    private void NavTab_Click(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        SwitchTab(sender);
    }

    private void NavTab_Checked(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        SwitchTab(sender);
    }

    private void NavTab_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_isInitialized) return;
        SwitchTab(sender);
    }

    private void SwitchTab(object sender)
    {
        if (FilesView == null || TransferView == null || StorageView == null || DiagnosticsView == null || ToolsView == null)
            return;

        HapticAudio.PlayClick();

        if (sender == TabFilesRadio)
        {
            TabFilesRadio.IsChecked = true;
            TabTransferRadio.IsChecked = false;
            TabStorageRadio.IsChecked = false;
            TabDiagnosticsRadio.IsChecked = false;
            TabToolsRadio.IsChecked = false;
        }
        else if (sender == TabTransferRadio)
        {
            TabFilesRadio.IsChecked = false;
            TabTransferRadio.IsChecked = true;
            TabStorageRadio.IsChecked = false;
            TabDiagnosticsRadio.IsChecked = false;
            TabToolsRadio.IsChecked = false;
        }
        else if (sender == TabStorageRadio)
        {
            TabFilesRadio.IsChecked = false;
            TabTransferRadio.IsChecked = false;
            TabStorageRadio.IsChecked = true;
            TabDiagnosticsRadio.IsChecked = false;
            TabToolsRadio.IsChecked = false;
        }
        else if (sender == TabDiagnosticsRadio)
        {
            TabFilesRadio.IsChecked = false;
            TabTransferRadio.IsChecked = false;
            TabStorageRadio.IsChecked = false;
            TabDiagnosticsRadio.IsChecked = true;
            TabToolsRadio.IsChecked = false;
        }
        else if (sender == TabToolsRadio)
        {
            TabFilesRadio.IsChecked = false;
            TabTransferRadio.IsChecked = false;
            TabStorageRadio.IsChecked = false;
            TabDiagnosticsRadio.IsChecked = false;
            TabToolsRadio.IsChecked = true;
        }

        UIElement activeView = FilesView;
        if (TabTransferRadio.IsChecked == true) activeView = TransferView;
        else if (TabStorageRadio.IsChecked == true) activeView = StorageView;
        else if (TabDiagnosticsRadio.IsChecked == true) activeView = DiagnosticsView;
        else if (TabToolsRadio.IsChecked == true) activeView = ToolsView;

        FilesView.Visibility = activeView == FilesView ? Visibility.Visible : Visibility.Collapsed;
        TransferView.Visibility = activeView == TransferView ? Visibility.Visible : Visibility.Collapsed;
        StorageView.Visibility = activeView == StorageView ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsView.Visibility = activeView == DiagnosticsView ? Visibility.Visible : Visibility.Collapsed;
        ToolsView.Visibility = activeView == ToolsView ? Visibility.Visible : Visibility.Collapsed;

        SmoothFadeIn(activeView);

        if (DiagnosticsView.Visibility == Visibility.Visible)
        {
            RefreshDiagnosticsUI();
        }
    }

    private static void SmoothFadeIn(UIElement element)
    {
        var anim = new System.Windows.Media.Animation.DoubleAnimation(0.3, 1.0, TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };
        element.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private void RefreshDiagnosticsUI()
    {
        var disks = HardwareAnalyzer.GetPhysicalDisks();
        DiagDisksList.ItemsSource = disks;

        var sys = SystemResourceMonitor.GetSnapshot();
        DiagCpuName.Text = $"Intel Core ({HardwareAnalyzer.LogicalCoreCount} потоков)";
        DiagCpuProgress.Value = sys.CpuTotalPercent;
        DiagCpuLoadText.Text = $"Загрузка CPU: {sys.CpuTotalPercent:F0}%";

        DiagRamText.Text = $"{sys.TotalMemoryGb:F1} ГБ RAM ({sys.MemoryUsagePercent:F0}% занято)";
        DiagRamProgress.Value = sys.MemoryUsagePercent;
        DiagRamDetailText.Text = $"Свободно физической памяти: {sys.AvailableMemoryGb:F1} ГБ";

        if (BenchTargetCombo.Items.Count == 0)
        {
            foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
            {
                BenchTargetCombo.Items.Add(d.RootDirectory.FullName);
            }
            if (BenchTargetCombo.Items.Count > 0) BenchTargetCombo.SelectedIndex = 0;
        }
    }

    private async void RunBenchmark_Click(object sender, RoutedEventArgs e)
    {
        string target = BenchTargetCombo.SelectedItem?.ToString() ?? Path.GetTempPath();
        HapticAudio.PlayClick();
        RunBenchBtn.IsEnabled = false;
        BenchProgress.Visibility = Visibility.Visible;
        BenchResultsList.Visibility = Visibility.Collapsed;
        BenchStatusText.Text = "Инициализация тестов…";

        var statusProg = new Progress<string>(s => BenchStatusText.Text = s);
        var pctProg = new Progress<double>(p => BenchProgress.Value = p);

        try
        {
            var report = await BenchmarkEngine.RunFullBenchmarkAsync(target, statusProg, pctProg);
            HapticAudio.PlaySuccess();
            BenchResultsList.ItemsSource = report.Results;
            BenchResultsList.Visibility = Visibility.Visible;
            BenchStatusText.Text = $"Тестирование завершено! Общий индекс производительности: {report.OverallScore:F0}";
        }
        catch (Exception ex)
        {
            BenchStatusText.Text = $"Ошибка бенчмарка: {ex.Message}";
        }
        finally
        {
            RunBenchBtn.IsEnabled = true;
            BenchProgress.Visibility = Visibility.Collapsed;
        }
    }

    // ---------- БЫСТРЫЙ ДОСТУП К ОКНУ НАСТРОЕК ----------

    private void OpenSettingsWindow_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        var dlg = new SettingsWindow { Owner = this };
        dlg.ShowDialog();
    }

    // ---------- ДВИЖОК ПЕРЕДАЧ (TAB 2) ----------

    private void InitSpeedGraph()
    {
        _speedSamples.Clear();
        for (int i = 0; i < 60; i++) _speedSamples.Enqueue(0);

        _speedGraphTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33) // ~30 FPS
        };
        _speedGraphTimer.Tick += (_, _) =>
        {
            if (_speedSamples.Count >= 60)
            {
                _speedSamples.Dequeue();
            }
            _speedSamples.Enqueue(_currentSpeedMb);
            RenderSpeedGraph();
        };
        _speedGraphTimer.Start();
    }

    private void LiveSpeedCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RenderSpeedGraph();
    }

    private void RenderSpeedGraph()
    {
        if (LiveSpeedCanvas == null || LiveSpeedCanvas.ActualWidth <= 10 || LiveSpeedCanvas.ActualHeight <= 10)
            return;

        LiveSpeedCanvas.Children.Clear();

        double w = LiveSpeedCanvas.ActualWidth;
        double h = LiveSpeedCanvas.ActualHeight;

        double maxVal = Math.Max(500.0, _speedSamples.Max() * 1.25);
        if (GraphMaxScaleText != null)
        {
            GraphMaxScaleText.Text = maxVal >= 1000 ? $"Шкала: {maxVal / 1000:F1} ГБ/с" : $"Шкала: {maxVal:F0} МБ/с";
        }

        // Горизонтальная сетка (3 деления)
        var gridBrush = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));
        for (int i = 1; i <= 3; i++)
        {
            double y = h * (1.0 - (i / 4.0));
            var line = new System.Windows.Shapes.Line
            {
                X1 = 0, Y1 = y, X2 = w, Y2 = y,
                Stroke = gridBrush,
                StrokeDashArray = new DoubleCollection { 4, 4 }
            };
            LiveSpeedCanvas.Children.Add(line);
        }

        var samples = _speedSamples.ToArray();
        if (samples.Length < 2) return;

        var points = new PointCollection();
        double stepX = w / (samples.Length - 1);

        for (int i = 0; i < samples.Length; i++)
        {
            double x = i * stepX;
            double norm = Math.Clamp(samples[i] / maxVal, 0.0, 1.0);
            double y = h - (norm * (h - 14)) - 4;
            points.Add(new Point(x, y));
        }

        // Заливка под графиком
        var polyPoints = new PointCollection(points)
        {
            new Point(w, h),
            new Point(0, h)
        };

        var fillPolygon = new System.Windows.Shapes.Polygon
        {
            Points = polyPoints,
            Fill = new LinearGradientBrush(
                Color.FromArgb(70, 0, 240, 255),
                Color.FromArgb(4, 0, 240, 255),
                new Point(0, 0),
                new Point(0, 1))
        };
        LiveSpeedCanvas.Children.Add(fillPolygon);

        // Линия графика
        var speedLine = new System.Windows.Shapes.Polyline
        {
            Points = points,
            Stroke = (Brush)FindResource("AccentBrush"),
            StrokeThickness = 2.5
        };
        LiveSpeedCanvas.Children.Add(speedLine);
    }

    private void BrowseSourceFile_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        var ofd = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите файл для передачи"
        };
        if (ofd.ShowDialog() == true)
        {
            TransferSourceBox.Text = ofd.FileName;
        }
    }

    private void BrowseSourceFolder_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        var folder = CyberFolderPickerDialog.PickFolder(this, "ВЫБОР ИСХОДНОЙ ПАПКИ ДЛЯ ПЕРЕДАЧИ", TransferSourceBox.Text);
        if (!string.IsNullOrEmpty(folder))
        {
            TransferSourceBox.Text = folder;
        }
    }

    private void BrowseDestFolder_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        var folder = CyberFolderPickerDialog.PickFolder(this, "ВЫБОР ЦЕЛЕВОЙ ПАПКИ НАЗНАЧЕНИЯ", TransferDestBox.Text);
        if (!string.IsNullOrEmpty(folder))
        {
            TransferDestBox.Text = folder;
        }
    }

    private void TransferPath_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void TransferSource_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            HapticAudio.PlayClick();
            TransferSourceBox.Text = files[0];
        }
    }

    private void TransferDest_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            HapticAudio.PlayClick();
            string p = files[0];
            TransferDestBox.Text = Directory.Exists(p) ? p : (Path.GetDirectoryName(p) ?? p);
        }
    }

    private MotionCopyWindow? _activeMotionWindow;

    private void OpenMotionWindow_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();

        if (_activeMotionWindow != null && _activeMotionWindow.IsVisible && _activeMotionWindow.Engine.IsRunning)
        {
            _activeMotionWindow.Activate();
            return;
        }

        string src = TransferSourceBox?.Text?.Trim() ?? "";
        string dst = TransferDestBox?.Text?.Trim() ?? "";

        if (!string.IsNullOrEmpty(src) && (File.Exists(src) || Directory.Exists(src)) &&
            !string.IsNullOrEmpty(dst) && Directory.Exists(dst))
        {
            _ = LaunchRealTransferAsync(src, dst);
        }
        else
        {
            StartLiveTransfer_Click(sender, e);
        }
    }

    private async void StartLiveTransfer_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();

        string src = TransferSourceBox?.Text?.Trim() ?? "";
        string dst = TransferDestBox?.Text?.Trim() ?? "";

        // Если источник не указан — запрашиваем файл или папку
        if (string.IsNullOrEmpty(src) || (!File.Exists(src) && !Directory.Exists(src)))
        {
            var ofd = new Microsoft.Win32.OpenFileDialog { Title = "Выберите исходный файл (или нажмите Отмена для выбора папки)" };
            if (ofd.ShowDialog() == true)
            {
                src = ofd.FileName;
                TransferSourceBox.Text = src;
            }
            else
            {
                var pickedSrc = Win11CopyDialog.Views.Dialogs.CyberFolderPickerDialog.PickFolder(this, "Выберите исходную папку для передачи");
                if (!string.IsNullOrEmpty(pickedSrc))
                {
                    src = pickedSrc;
                    TransferSourceBox.Text = src;
                }
                else
                {
                    MessageBox.Show("Пожалуйста, выберите исходный файл или папку для передачи.", "Выбор источника", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
            }
        }

        // Если приёмник не указан — запрашиваем папку назначения
        if (string.IsNullOrEmpty(dst) || !Directory.Exists(dst))
        {
            var pickedDst = Win11CopyDialog.Views.Dialogs.CyberFolderPickerDialog.PickFolder(this, "Выберите целевую папку (куда передавать данные)");
            if (!string.IsNullOrEmpty(pickedDst))
            {
                dst = pickedDst;
                TransferDestBox.Text = dst;
            }
            else
            {
                MessageBox.Show("Пожалуйста, выберите папку назначения для передачи.", "Выбор назначения", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }

        await LaunchRealTransferAsync(src, dst);
    }

    private async Task LaunchRealTransferAsync(string src, string dst)
    {
        StartTransferBtn.IsEnabled = false;
        PauseTransferBtn.IsEnabled = true;
        CancelTransferBtn.IsEnabled = true;
        _transferIsPaused = false;
        PauseTransferBtn.Content = "⏸ Пауза";

        _peakSpeedMb = 0;
        _currentSpeedMb = 0;
        LiveTransferProgressBar.Value = 0;

        _transferCts = new CancellationTokenSource();
        var ct = _transferCts.Token;

        var motion = new MotionCopyWindow { Owner = this };
        _activeMotionWindow = motion;
        motion.Show();
        motion.Activate();

        var sw = Stopwatch.StartNew();

        void OnProgressTick(object? s, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                double speedMb = motion.Engine.CurrentSpeed / (1024.0 * 1024.0);
                _currentSpeedMb = speedMb;
                if (speedMb > _peakSpeedMb) _peakSpeedMb = speedMb;

                double pct = motion.Engine.OverallProgress;
                LiveTransferProgressBar.Value = pct;

                HudCurrentSpeedText.Text = $"{speedMb:F1} МБ/с";
                HudPeakSpeedText.Text = $"{_peakSpeedMb:F1} МБ/с";
                HudProgressText.Text = $"{pct:F0}% ({motion.Engine.DoneCount} / {motion.Engine.Items.Count})";
                HudBytesText.Text = $"{Formatters.Bytes(motion.Engine.CopiedBytes)} из {Formatters.Bytes(motion.Engine.TotalBytes)}";
                HudEtaText.Text = Formatters.Eta(motion.Engine.Eta);

                double avgSpeed = (motion.Engine.CopiedBytes / (1024.0 * 1024.0)) / Math.Max(0.1, sw.Elapsed.TotalSeconds);
                HudAvgSpeedText.Text = $"Средняя: {avgSpeed:F1} МБ/с";

                UpdateBottleneckIndicator(speedMb);
            });
        }

        motion.Engine.ProgressTick += OnProgressTick;

        try
        {
            await motion.StartRealTransferAsync(src, dst, ct);

            if (motion.Engine.IsCompleted)
            {
                LiveTransferProgressBar.Value = 100;
                _currentSpeedMb = 0;
                HudCurrentSpeedText.Text = "0.0 МБ/с";
                HudEtaText.Text = "00:00:00";
                HudProgressText.Text = $"100% ({motion.Engine.DoneCount} / {motion.Engine.Items.Count})";
                HudBytesText.Text = $"{Formatters.Bytes(motion.Engine.TotalBytes)} из {Formatters.Bytes(motion.Engine.TotalBytes)}";
                HapticAudio.PlaySuccess();
            }
        }
        catch (OperationCanceledException)
        {
            _currentSpeedMb = 0;
            HudCurrentSpeedText.Text = "Прервано";
            HudEtaText.Text = "--:--:--";
        }
        catch (Exception ex)
        {
            _currentSpeedMb = 0;
            MessageBox.Show($"Ошибка передачи данных: {ex.Message}", "Ошибка ввода-вывода", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            motion.Engine.ProgressTick -= OnProgressTick;
            StartTransferBtn.IsEnabled = true;
            PauseTransferBtn.IsEnabled = false;
            CancelTransferBtn.IsEnabled = false;
        }
    }

    private void PauseLiveTransfer_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        if (_activeMotionWindow?.Engine != null)
        {
            if (_activeMotionWindow.Engine.IsPaused)
            {
                _activeMotionWindow.Engine.Resume();
                _transferIsPaused = false;
                PauseTransferBtn.Content = "⏸ Пауза";
            }
            else
            {
                _activeMotionWindow.Engine.Pause();
                _transferIsPaused = true;
                PauseTransferBtn.Content = "▶ Продолжить";
                _currentSpeedMb = 0;
                BottleneckStatusText.Text = "⏸ Поток данных временно приостановлен пользователем.";
            }
        }
    }

    private void CancelLiveTransfer_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        _activeMotionWindow?.Engine?.Cancel();
        _transferCts?.Cancel();
    }

    private void UpdateBottleneckIndicator(double speedMb)
    {
        if (BottleneckStatusText == null) return;

        if (speedMb >= 1000)
        {
            BottleneckStatusText.Text = "🚀 NVMe Gen4/Gen5 Direct I/O • Узких мест не обнаружено • Аппаратное насыщение шины";
            BottleneckStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0, 240, 255));
        }
        else if (speedMb >= 350)
        {
            BottleneckStatusText.Text = "⚡ SATA SSD / NVMe Gen3 • Высокая пропускная способность • Буферизация 100%";
            BottleneckStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0, 230, 118));
        }
        else if (speedMb >= 100)
        {
            BottleneckStatusText.Text = "⚙ Умеренная скорость передачи • Сбалансированный ввод-вывод";
            BottleneckStatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 214, 0));
        }
        else
        {
            BottleneckStatusText.Text = "🐢 Ограничение скорости накопителя (HDD / USB 2.0) или большое количество мелких файлов";
            BottleneckStatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 82, 82));
        }
    }

    public static List<(string, long)> DefaultMixedScenario()
    {
        var list = new List<(string, long)>();
        for (int i = 1; i <= 5; i++) list.Add(($"VID_2026_{i:D2}.mp4", 650_000_000L + i * 80_000_000L));
        for (int i = 1; i <= 80; i++) list.Add(($"IMG_2026_{i:D3}.jpg", 3_500_000L + (i * 37) % 7_000_000L));
        for (int i = 1; i <= 35; i++) list.Add(($"DOC_report_{i}.pdf", 400_000L + (i * 29) % 2_500_000L));
        for (int i = 1; i <= 8; i++) list.Add(($"project_backup_{i}.zip", 180_000_000L + i * 45_000_000L));
        return list;
    }

    // ---------- ИНСТРУМЕНТЫ (TAB 3) ----------

    private void OpenWizTree_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        new WizTreeAnalyzerWindow(_currentPath) { Owner = this }.Show();
    }

    private void OpenDuplicateFinder_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        new DuplicateFinderWindow(_currentPath) { Owner = this }.Show();
    }

    private void OpenDriverInspector_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        new DriverInspectorWindow { Owner = this }.Show();
    }

    private void OpenChecksumTool_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        new AdvancedToolsWindow { Owner = this }.ShowDialog();
    }

    private void OpenFileCompare_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        var dlg = new AdvancedToolsWindow { Owner = this };
        dlg.ShowDialog();
    }

    private void OpenDiskAnalyze_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        new AdvancedToolsWindow(_currentPath) { Owner = this }.ShowDialog();
    }

    private void QuickTheme_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        // Toggle light/dark logic can be implemented here or delegated to ThemeManager
    }

    // ---------- НАСТРОЙКИ (TAB 4) ----------

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject dep)
        {
            if (FindVisualParent<System.Windows.Controls.Primitives.ButtonBase>(dep) != null)
                return;
        }

        if (e.ChangedButton == MouseButton.Left)
        {
            if (e.ClickCount == 2)
            {
                Max_Click(sender, e);
            }
            else
            {
                DragMove();
            }
        }
    }

    private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        DependencyObject? current = child;
        while (current != null)
        {
            if (current is T match) return match;
            if (current is System.Windows.Media.Visual || current is System.Windows.Media.Media3D.Visual3D)
                current = System.Windows.Media.VisualTreeHelper.GetParent(current);
            else
                current = LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Max_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        MaxBtn.Content = WindowState == WindowState.Maximized ? "❐" : "□";
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // Settings Handlers (Stubs)
    private void SettingsThemeCombo_Changed(object sender, SelectionChangedEventArgs e) { }
    private void SettingsBackdrop_Changed(object sender, RoutedEventArgs e) { }
    private void SettingsHaptics_Changed(object sender, RoutedEventArgs e) { }
    private void SettingsTestClick_Click(object sender, RoutedEventArgs e) { }
    private void SettingsTestHover_Click(object sender, RoutedEventArgs e) { }
    private void SettingsTestSuccess_Click(object sender, RoutedEventArgs e) { }
    private void SettingsTestScroll_Click(object sender, RoutedEventArgs e) { }
    private void SettingsOpenConfigFolder_Click(object sender, RoutedEventArgs e) { }
    private void SettingsReloadConfig_Click(object sender, RoutedEventArgs e) { }
    private void SettingsResetConfig_Click(object sender, RoutedEventArgs e) { }
    private void SettingsSaveConfig_Click(object sender, RoutedEventArgs e) { }
}
