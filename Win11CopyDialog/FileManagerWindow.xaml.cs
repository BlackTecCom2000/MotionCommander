using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Win11CopyDialog.Helpers;
using Win11CopyDialog.Models;

namespace Win11CopyDialog;

public partial class FileManagerWindow : Window, INotifyPropertyChanged
{
    private readonly List<string> _backHistory = new();
    private readonly List<string> _forwardHistory = new();
    private string _currentPath = "";
    private CancellationTokenSource? _copyCts;
    private bool _isCopyPaused;

    public FileManagerWindow()
    {
        InitializeComponent();
        BackdropHelper.Apply(this, ThemeManager.Instance.Backdrop, ThemeManager.Instance.IsDark);
        Loaded += FileManagerWindow_Loaded;
        Closed += FileManagerWindow_Closed;
    }

    private async void FileManagerWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        RootBorder.BeginAnimation(OpacityProperty, fade);
        RootScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, new DoubleAnimation(0.97, 1, TimeSpan.FromMilliseconds(280)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        RootScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, new DoubleAnimation(0.97, 1, TimeSpan.FromMilliseconds(280)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        await FolderTree.LoadDrivesAsync();
        
        // Start at user profile
        var startPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        await NavigateToAsync(startPath);
    }

    private void FileManagerWindow_Closed(object? sender, EventArgs e)
    {
        _copyCts?.Cancel();
        _copyCts?.Dispose();
    }

    private async Task NavigateToAsync(string path, bool addToHistory = true)
    {
        if (!Directory.Exists(path)) return;

        try
        {
            if (addToHistory && _currentPath != path)
            {
                if (!string.IsNullOrEmpty(_currentPath))
                    _backHistory.Add(_currentPath);
                _forwardHistory.Clear();
                UpdateNavButtons();
            }

            _currentPath = path;
            CurrentPathDisplay = path;
            AddressBar.Text = path;

            await LoadFolderAsync(path);
            UpdateFreeSpace(path);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Ошибка: {ex.Message}";
        }
    }

    private async Task LoadFolderAsync(string path)
    {
        StatusText.Text = "Загрузка...";
        try
        {
            var entries = await Task.Run(() =>
            {
                var result = new List<FileEntry>();
                try
                {
                    foreach (var dir in Directory.GetDirectories(path))
                    {
                        try
                        {
                            var info = new DirectoryInfo(dir);
                            if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                            var entry = new FileEntry(dir, FileEntryType.Folder, info.LastWriteTime, info.CreationTime, info.Attributes);
                            entry.SetIcon(FileSystemIcons.GetIcon(dir, FileEntryType.Folder));
                            result.Add(entry);
                        }
                        catch { }
                    }
                    foreach (var file in Directory.GetFiles(path))
                    {
                        try
                        {
                            var info = new FileInfo(file);
                            if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                            var entry = new FileEntry(file, FileEntryType.File, info.LastWriteTime, info.CreationTime, info.Attributes);
                            entry.SetSize(info.Length);
                            entry.SetIcon(FileSystemIcons.GetIcon(file, FileEntryType.File));
                            result.Add(entry);
                        }
                        catch { }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    Application.Current.Dispatcher.Invoke(() => StatusText.Text = "Нет доступа к папке");
                }
                return result;
            });

            var observableEntries = new ObservableCollection<FileEntry>(entries);
            FileList.Items = observableEntries;
            FileList.CurrentPath = path;
            StatusText.Text = $"{entries.Count} объектов";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Ошибка загрузки: {ex.Message}";
        }
    }

    private void UpdateFreeSpace(string path)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(path)!);
            if (drive.IsReady)
            {
                FreeSpaceText.Text = $"Свободно: {Formatters.Bytes(drive.AvailableFreeSpace)} из {Formatters.Bytes(drive.TotalSize)}";
            }
        }
        catch { FreeSpaceText.Text = ""; }
    }

    private void UpdateNavButtons()
    {
        BtnBack.IsEnabled = _backHistory.Count > 0;
        BtnForward.IsEnabled = _forwardHistory.Count > 0;
    }

    private void FolderTree_FolderSelected(FileEntry entry)
    {
        if (entry.Type == FileEntryType.Folder || entry.Type == FileEntryType.Drive)
            _ = NavigateToAsync(entry.FullPath);
    }

    private async void FileList_ItemActivated(FileEntry entry)
    {
        if (entry.Type == FileEntryType.Folder || entry.Type == FileEntryType.Drive)
        {
            await NavigateToAsync(entry.FullPath);
        }
        else
        {
            // Open file with default application
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(entry.FullPath) { UseShellExecute = true }); }
            catch { }
        }
    }

    private void FileList_SelectionChanged(FileEntry[] selected)
    {
        if (selected.Length == 0)
            SelectionText.Text = "";
        else if (selected.Length == 1)
        {
            var e = selected[0];
            SelectionText.Text = $"{e.Name} • {e.SizeText} • {e.LastWriteTime:dd.MM.yyyy HH:mm}";
        }
        else
        {
            long totalSize = selected.Where(f => f.Type == FileEntryType.File).Sum(f => f.SizeBytes);
            SelectionText.Text = $"{selected.Length} выбрано • {Formatters.Bytes(totalSize)}";
        }
    }

    private void AddressBar_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var path = AddressBar.Text.Trim();
            if (Directory.Exists(path))
                _ = NavigateToAsync(path);
            else
                StatusText.Text = "Путь не найден";
        }
    }

    private void AddressBar_GotFocus(object sender, RoutedEventArgs e)
    {
        AddressBar.SelectAll();
    }

    private void BtnBack_Click(object sender, RoutedEventArgs e)
    {
        if (_backHistory.Count > 0)
        {
            var prev = _backHistory[_backHistory.Count - 1];
            _backHistory.RemoveAt(_backHistory.Count - 1);
            _forwardHistory.Add(_currentPath);
            _ = NavigateToAsync(prev, false);
            UpdateNavButtons();
        }
    }

    private void BtnForward_Click(object sender, RoutedEventArgs e)
    {
        if (_forwardHistory.Count > 0)
        {
            var next = _forwardHistory[_forwardHistory.Count - 1];
            _forwardHistory.RemoveAt(_forwardHistory.Count - 1);
            _backHistory.Add(_currentPath);
            _ = NavigateToAsync(next, false);
            UpdateNavButtons();
        }
    }

    private void BtnUp_Click(object sender, RoutedEventArgs e)
    {
        var parent = Directory.GetParent(_currentPath);
        if (parent != null)
            _ = NavigateToAsync(parent.FullName);
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        _ = NavigateToAsync(_currentPath, false);
    }

    private void ViewList_Click(object sender, RoutedEventArgs e) { /* switch to list view */ }
    private void ViewTiles_Click(object sender, RoutedEventArgs e) { /* switch to tiles view */ }

    private void NewWindow_Click(object sender, RoutedEventArgs e)
    {
        var win = new FileManagerWindow();
        win.Show();
    }

    // Copy with Motion progress overlay
    public async void StartCopy(FileEntry[] sources, string destDir)
    {
        if (sources.Length == 0) return;

        _copyCts = new CancellationTokenSource();
        _isCopyPaused = false;
        _copyStart = DateTime.Now;

        CopyOverlay.Visibility = Visibility.Visible;
        CopyOverlay.Opacity = 0;
        CopyOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));

        var progress = new Progress<CopyProgress>(p => Dispatcher.Invoke(() => UpdateCopyUI(p)));
        
        try
        {
            foreach (var source in sources)
            {
                _copyCts.Token.ThrowIfCancellationRequested();
                await WaitIfPausedAsync();
                
                CopyFileName.Text = source.Name;
                await FileService.CopyAsync(source, destDir, progress, _copyCts.Token);
            }
            
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300));
            fadeOut.Completed += (_, _) => CopyOverlay.Visibility = Visibility.Collapsed;
            CopyOverlay.BeginAnimation(OpacityProperty, fadeOut);
            StatusText.Text = "Копирование завершено";
            await LoadFolderAsync(_currentPath);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Копирование отменено";
            CopyOverlay.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Ошибка: {ex.Message}";
            CopyOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateCopyUI(CopyProgress p)
    {
        CopyProgressBar.Value = p.Percent;
        CopySpeedText.Text = p.TotalBytes > 0 ? $"{Formatters.Speed(p.CopiedBytes / Math.Max(1, (DateTime.Now - _copyStart).TotalSeconds))}" : "";
        CopyEtaText.Text = p.TotalBytes > 0 && p.CopiedBytes > 0 
            ? $"Осталось: {Formatters.Eta(TimeSpan.FromSeconds((p.TotalBytes - p.CopiedBytes) / Math.Max(1, p.CopiedBytes / (DateTime.Now - _copyStart).TotalSeconds)))}"
            : "";
    }

    private DateTime _copyStart;
    private async Task WaitIfPausedAsync()
    {
        while (_isCopyPaused && _copyCts != null && !_copyCts.Token.IsCancellationRequested)
            await Task.Delay(100, _copyCts.Token);
    }

    private void BtnPauseCopy_Click(object sender, RoutedEventArgs e)
    {
        _isCopyPaused = !_isCopyPaused;
        BtnPauseCopy.Content = _isCopyPaused ? "▶ Продолжить" : "❚❚ Пауза";
    }

    private void BtnCancelCopy_Click(object sender, RoutedEventArgs e)
    {
        _copyCts?.Cancel();
    }

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Max_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    public string CurrentPathDisplay
    {
        get => _currentPath;
        set { _currentPath = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}