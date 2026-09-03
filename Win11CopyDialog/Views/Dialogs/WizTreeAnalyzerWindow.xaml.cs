using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using Win11CopyDialog.Helpers;
using Win11CopyDialog.Models;

namespace Win11CopyDialog.Views.Dialogs;

public sealed class WizNode
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long SizeBytes { get; set; }
    public double PercentOfParent { get; set; }
    public string PercentText => $"{PercentOfParent:F1}%";
    public string FormattedSize => Formatters.Bytes(SizeBytes);
    public string Icon => IsDirectory ? "📁" : GetFileIcon(Name);
    public List<WizNode> Children { get; set; } = new();

    public static string GetFileIcon(string name)
    {
        string ext = Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".mp4" or ".mkv" or ".avi" or ".mov" => "🎬",
            ".zip" or ".7z" or ".rar" or ".tar" or ".gz" => "🗜",
            ".iso" or ".img" or ".vhd" => "💿",
            ".exe" or ".msi" => "⚙",
            ".jpg" or ".jpeg" or ".png" or ".webp" => "🖼",
            ".mp3" or ".flac" or ".wav" => "🎵",
            ".pdf" or ".doc" or ".docx" or ".txt" => "📄",
            _ => "📄"
        };
    }
}

public sealed class WizFileInfo
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string Extension { get; set; } = "";
    public long SizeBytes { get; set; }
    public string FormattedSize => Formatters.Bytes(SizeBytes);
    public string Icon => WizNode.GetFileIcon(Name);
}

public sealed class WizExtInfo
{
    public string Extension { get; set; } = "";
    public long TotalBytes { get; set; }
    public int FileCount { get; set; }
    public double Percent { get; set; }
    public string PercentText => $"{Percent:F1}%";
    public string FormattedSize => Formatters.Bytes(TotalBytes);
}

public partial class WizTreeAnalyzerWindow : Window
{
    private CancellationTokenSource? _scanCts;
    private bool _isScanning;

    public WizTreeAnalyzerWindow(string? initialPath = null)
    {
        InitializeComponent();
        ThemeManager.Instance.Apply();
        BackdropHelper.Apply(this, ThemeManager.Instance.Backdrop, ThemeManager.Instance.IsDark);

        TargetFolderBox.Text = string.IsNullOrEmpty(initialPath) ? "C:\\" : initialPath;
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        var dlg = new OpenFolderDialog
        {
            Title = "Выберите диск или папку для анализа",
            InitialDirectory = Directory.Exists(TargetFolderBox.Text) ? TargetFolderBox.Text : "C:\\"
        };
        if (dlg.ShowDialog() == true)
        {
            TargetFolderBox.Text = dlg.FolderName;
        }
    }

    private async void StartScan_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();

        if (_isScanning)
        {
            _scanCts?.Cancel();
            return;
        }

        string root = TargetFolderBox.Text.Trim();
        if (!Directory.Exists(root))
        {
            MessageBox.Show("Указанная директория не найдена.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _isScanning = true;
        ScanBtn.Content = "⏹ Прервать";
        ScanProgressBar.Visibility = Visibility.Visible;
        StatusText.Text = "Сканирование структуры дискового пространства...";

        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        var allFiles = new List<WizFileInfo>();

        try
        {
            var sw = Stopwatch.StartNew();
            var rootNode = await Task.Run(() => ScanDirectoryTree(root, allFiles, ct, p =>
            {
                Dispatcher.InvokeAsync(() => StatusText.Text = $"Сканирование: {p}");
            }), ct);

            sw.Stop();

            if (rootNode != null)
            {
                FoldersTreeView.ItemsSource = new List<WizNode> { rootNode };

                // Топ 100 тяжелых файлов
                var top100 = allFiles.OrderByDescending(f => f.SizeBytes).Take(100).ToList();
                TopFilesList.ItemsSource = top100;

                // Статистика по типам
                long totalBytes = rootNode.SizeBytes > 0 ? rootNode.SizeBytes : 1;
                var extStats = allFiles
                    .GroupBy(f => string.IsNullOrEmpty(f.Extension) ? "(без расширения)" : f.Extension.ToLowerInvariant())
                    .Select(g => new WizExtInfo
                    {
                        Extension = g.Key,
                        TotalBytes = g.Sum(x => x.SizeBytes),
                        FileCount = g.Count(),
                        Percent = (double)g.Sum(x => x.SizeBytes) / totalBytes * 100.0
                    })
                    .OrderByDescending(x => x.TotalBytes)
                    .Take(50)
                    .ToList();

                ExtensionsList.ItemsSource = extStats;

                StatusText.Text = $"✔ Анализ завершён за {sw.Elapsed.TotalSeconds:F1} сек! Просканировано: {allFiles.Count:N0} файлов ({Formatters.Bytes(rootNode.SizeBytes)}).";
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Сканирование прервано пользователем.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Ошибка: {ex.Message}";
        }
        finally
        {
            _isScanning = false;
            ScanBtn.Content = "⚡ Начать сканирование";
            ScanProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private static WizNode ScanDirectoryTree(string path, List<WizFileInfo> allFilesCollector, CancellationToken ct, Action<string> onProgress)
    {
        var dirInfo = new DirectoryInfo(path);
        var node = new WizNode
        {
            Name = dirInfo.Parent == null ? dirInfo.FullName : dirInfo.Name,
            FullPath = dirInfo.FullName,
            IsDirectory = true
        };

        long dirTotal = 0;

        try
        {
            ct.ThrowIfCancellationRequested();
            onProgress(dirInfo.FullName);

            // Файлы
            foreach (var file in dirInfo.EnumerateFiles())
            {
                ct.ThrowIfCancellationRequested();
                long fLen = file.Length;
                dirTotal += fLen;

                var fNode = new WizNode
                {
                    Name = file.Name,
                    FullPath = file.FullName,
                    IsDirectory = false,
                    SizeBytes = fLen
                };
                node.Children.Add(fNode);

                lock (allFilesCollector)
                {
                    allFilesCollector.Add(new WizFileInfo
                    {
                        Name = file.Name,
                        FullPath = file.FullName,
                        Extension = file.Extension,
                        SizeBytes = fLen
                    });
                }
            }

            // Подкаталоги
            foreach (var sub in dirInfo.EnumerateDirectories())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // Пропуск системных защищенных папок, чтобы не зависать
                    if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    var subNode = ScanDirectoryTree(sub.FullName, allFilesCollector, ct, onProgress);
                    dirTotal += subNode.SizeBytes;
                    node.Children.Add(subNode);
                }
                catch { }
            }
        }
        catch { }

        node.SizeBytes = dirTotal;

        // Расчет процентов относительно родителя
        if (dirTotal > 0)
        {
            foreach (var c in node.Children)
            {
                c.PercentOfParent = (double)c.SizeBytes / dirTotal * 100.0;
            }
            node.Children = node.Children.OrderByDescending(c => c.SizeBytes).ToList();
        }

        return node;
    }

    private void TopFiles_OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (TopFilesList.SelectedItem is WizFileInfo f && File.Exists(f.FullPath))
        {
            Process.Start("explorer.exe", $"/select,\"{f.FullPath}\"");
        }
    }

    private void TopFiles_Delete_Click(object sender, RoutedEventArgs e)
    {
        if (TopFilesList.SelectedItem is WizFileInfo f && File.Exists(f.FullPath))
        {
            if (MessageBox.Show($"Удалить файл {f.Name} ({f.FormattedSize})?", "Подтверждение удаления", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                try
                {
                    File.Delete(f.FullPath);
                    MessageBox.Show("Файл удалён.", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Ошибка удаления: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
    }
}
