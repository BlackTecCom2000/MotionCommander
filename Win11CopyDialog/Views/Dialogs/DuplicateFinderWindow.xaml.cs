using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using Microsoft.Win32;
using Win11CopyDialog.Helpers;
using Win11CopyDialog.Models;

namespace Win11CopyDialog.Views.Dialogs;

public sealed class DuplicateFileItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public int GroupIndex { get; set; }
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string DirectoryPath { get; set; } = "";
    public long SizeBytes { get; set; }
    public string FormattedSize => Formatters.Bytes(SizeBytes);
    public DateTime DateModified { get; set; }
    public string DateModifiedFormatted => DateModified.ToString("dd.MM.yyyy HH:mm");
    public string Hash { get; set; } = "";
    public bool IsOriginal { get; set; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged(nameof(IsSelected));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}

public partial class DuplicateFinderWindow : Window
{
    private CancellationTokenSource? _scanCts;
    private bool _isScanning;
    private readonly List<DuplicateFileItem> _items = new();

    public DuplicateFinderWindow(string? initialPath = null)
    {
        InitializeComponent();
        ThemeManager.Instance.Apply();
        BackdropHelper.Apply(this, ThemeManager.Instance.Backdrop, ThemeManager.Instance.IsDark);

        SearchFolderBox.Text = string.IsNullOrEmpty(initialPath) ? "C:\\" : initialPath;
        UpdateSelectionCount();
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        var dlg = new OpenFolderDialog
        {
            Title = "Выберите папку для поиска дубликатов",
            InitialDirectory = Directory.Exists(SearchFolderBox.Text) ? SearchFolderBox.Text : "C:\\"
        };
        if (dlg.ShowDialog() == true)
        {
            SearchFolderBox.Text = dlg.FolderName;
        }
    }

    private async void StartSearch_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();

        if (_isScanning)
        {
            _scanCts?.Cancel();
            return;
        }

        string root = SearchFolderBox.Text.Trim();
        if (!Directory.Exists(root))
        {
            MessageBox.Show("Указанная директория не найдена.", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        long minBytes = MinSizeCombo.SelectedIndex switch
        {
            1 => 1024 * 1024,                // > 1 MB
            2 => 10 * 1024 * 1024,           // > 10 MB
            3 => 100 * 1024 * 1024,          // > 100 MB
            4 => 1024L * 1024 * 1024,        // > 1 GB
            _ => 0                           // Any size
        };

        _isScanning = true;
        StartSearchBtn.Content = "⏹ Прервать";
        SearchProgress.Visibility = Visibility.Visible;
        StatusText.Text = "Поиск файлов и сбор размеров...";
        _items.Clear();
        DuplicatesListView.ItemsSource = null;

        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        var sw = Stopwatch.StartNew();

        try
        {
            var duplicates = await Task.Run(() => ScanDuplicates(root, minBytes, ct), ct);

            _items.Clear();
            _items.AddRange(duplicates);
            DuplicatesListView.ItemsSource = _items;

            int groupCount = duplicates.Select(d => d.GroupIndex).Distinct().Count();
            long duplicateWastedBytes = duplicates.Where(d => !d.IsOriginal).Sum(d => d.SizeBytes);

            StatusText.Text = $"Найдено групп: {groupCount} ({duplicates.Count} файлов, из них {duplicates.Count - groupCount} копий). Занимают: {Formatters.Bytes(duplicateWastedBytes)} за {sw.Elapsed.TotalSeconds:F1} сек.";
            UpdateSelectionCount();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Поиск дубликатов прерван пользователем.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Ошибка: {ex.Message}";
        }
        finally
        {
            _isScanning = false;
            StartSearchBtn.Content = "⚡ Искать дубликаты";
            SearchProgress.Visibility = Visibility.Collapsed;
        }
    }

    private List<DuplicateFileItem> ScanDuplicates(string rootPath, long minSize, CancellationToken ct)
    {
        var result = new List<DuplicateFileItem>();
        var sizeMap = new Dictionary<long, List<FileInfo>>();

        // Этап 1: Сбор файлов и группировка по размеру
        var dirQueue = new Queue<string>();
        dirQueue.Enqueue(rootPath);

        while (dirQueue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string curDir = dirQueue.Dequeue();

            try
            {
                var dirInfo = new DirectoryInfo(curDir);
                foreach (var di in dirInfo.EnumerateDirectories())
                {
                    // Пропускаем системные папки и корзину
                    if ((di.Attributes & (FileAttributes.ReparsePoint | FileAttributes.System)) != 0 &&
                        (di.Name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) ||
                         di.Name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }
                    dirQueue.Enqueue(di.FullName);
                }

                foreach (var fi in dirInfo.EnumerateFiles())
                {
                    ct.ThrowIfCancellationRequested();
                    if (fi.Length >= minSize && fi.Length > 0)
                    {
                        if (!sizeMap.TryGetValue(fi.Length, out var list))
                        {
                            list = new List<FileInfo>();
                            sizeMap[fi.Length] = list;
                        }
                        list.Add(fi);
                    }
                }
            }
            catch
            {
                // Игнорируем недоступные папки
            }
        }

        // Фильтруем: оставляем только размеры, где более 1 файла
        var candidateSizeGroups = sizeMap.Where(kv => kv.Value.Count > 1).ToList();

        // Этап 2 & 3: Пре-хэш первых 4 КБ и полный SHA-256
        int groupIndex = 1;

        foreach (var group in candidateSizeGroups)
        {
            ct.ThrowIfCancellationRequested();

            // Пре-хэш 4 КБ
            var preHashMap = new Dictionary<string, List<FileInfo>>();
            byte[] buffer = new byte[4096];

            foreach (var fi in group.Value)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var stream = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    int read = stream.Read(buffer, 0, buffer.Length);
                    string preHash = Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, read)));

                    if (!preHashMap.TryGetValue(preHash, out var preList))
                    {
                        preList = new List<FileInfo>();
                        preHashMap[preHash] = preList;
                    }
                    preList.Add(fi);
                }
                catch
                {
                    // Ошибка чтения
                }
            }

            // Этап 3: Полный SHA-256 для совпадающих пре-хэшей
            foreach (var preGroup in preHashMap.Where(kv => kv.Value.Count > 1))
            {
                ct.ThrowIfCancellationRequested();

                var fullHashMap = new Dictionary<string, List<FileInfo>>();

                foreach (var fi in preGroup.Value)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        using var stream = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var sha = SHA256.Create();
                        byte[] fullHashBytes = sha.ComputeHash(stream);
                        string fullHash = Convert.ToHexString(fullHashBytes);

                        if (!fullHashMap.TryGetValue(fullHash, out var fList))
                        {
                            fList = new List<FileInfo>();
                            fullHashMap[fullHash] = fList;
                        }
                        fList.Add(fi);
                    }
                    catch
                    {
                        // Ошибка чтения
                    }
                }

                foreach (var fullGroup in fullHashMap.Where(kv => kv.Value.Count > 1))
                {
                    int itemIdx = 0;
                    foreach (var fi in fullGroup.Value)
                    {
                        result.Add(new DuplicateFileItem
                        {
                            GroupIndex = groupIndex,
                            Name = fi.Name,
                            FullPath = fi.FullName,
                            DirectoryPath = fi.DirectoryName ?? "",
                            SizeBytes = fi.Length,
                            DateModified = fi.LastWriteTime,
                            Hash = fullGroup.Key,
                            IsOriginal = (itemIdx == 0),
                            IsSelected = false
                        });
                        itemIdx++;
                    }
                    groupIndex++;
                }
            }
        }

        // Сортировка: сначала самые тяжелые дубликаты
        return result.OrderByDescending(r => r.SizeBytes).ThenBy(r => r.GroupIndex).ToList();
    }

    private void SelectAllDuplicates_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        foreach (var item in _items)
        {
            // Отмечаем копии, оригиналы оставляем нетронутыми
            item.IsSelected = !item.IsOriginal;
        }
        UpdateSelectionCount();
    }

    private void DeselectAll_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        foreach (var item in _items)
        {
            item.IsSelected = false;
        }
        UpdateSelectionCount();
    }

    private void CheckChanged(object sender, RoutedEventArgs e)
    {
        UpdateSelectionCount();
    }

    private void UpdateSelectionCount()
    {
        int count = _items.Count(i => i.IsSelected);
        long bytes = _items.Where(i => i.IsSelected).Sum(i => i.SizeBytes);
        DeleteSelectedBtn.Content = $"🗑 Удалить выбранные ({count}) • {Formatters.Bytes(bytes)}";
        DeleteSelectedBtn.IsEnabled = count > 0;
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        var toDelete = _items.Where(i => i.IsSelected).ToList();
        if (toDelete.Count == 0) return;

        long totalBytes = toDelete.Sum(i => i.SizeBytes);
        var res = MessageBox.Show(
            $"Вы уверены, что хотите удалить {toDelete.Count} файлов-дубликатов?\n" +
            $"Будет освобождено: {Formatters.Bytes(totalBytes)}\n\n" +
            "Файлы будут удалены безвозвратно.",
            "Подтверждение удаления",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (res != MessageBoxResult.Yes) return;

        int deletedCount = 0;
        long freedBytes = 0;

        foreach (var item in toDelete)
        {
            try
            {
                if (File.Exists(item.FullPath))
                {
                    File.Delete(item.FullPath);
                    freedBytes += item.SizeBytes;
                    deletedCount++;
                    _items.Remove(item);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to delete {item.FullPath}: {ex.Message}");
            }
        }

        DuplicatesListView.ItemsSource = null;
        DuplicatesListView.ItemsSource = _items;
        UpdateSelectionCount();

        StatusText.Text = $"Успешно удалено: {deletedCount} файлов. Освобождено диска: {Formatters.Bytes(freedBytes)}.";
        MessageBox.Show($"Удалено файлов: {deletedCount}\nОсвобождено памяти: {Formatters.Bytes(freedBytes)}",
            "Очистка завершена", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
