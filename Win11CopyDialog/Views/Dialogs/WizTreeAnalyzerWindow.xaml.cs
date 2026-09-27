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

    /// <summary>
    /// Окно закрывается: с этого момента ни один фоновый поток scan не имеет
    /// права трогать элементы интерфейса.
    /// </summary>
    private bool _isClosing;

    public WizTreeAnalyzerWindow(string? initialPath = null)
    {
        InitializeComponent();
        ThemeManager.Instance.Apply();
        BackdropHelper.Apply(this, ThemeManager.Instance.Backdrop, ThemeManager.Instance.IsDark);

        TargetFolderBox.Text = string.IsNullOrEmpty(initialPath) ? "C:\\" : initialPath;

        // Закрытие возможно не только через кнопку ✕ / Close_Click, но и через
        // Alt+F4, системное меню и выключение приложения. Раньше _scanCts здесь
        // НЕ отменялся: полное сканирование C:\ продолжалось после закрытия
        // окна, держало замыкание на это окно и с 10 Гц дёргало Dispatcher,
        // чтобы обновить StatusText уже закрытого окна, а его finally правил
        // элементы мёртвого окна.
        Closing += Window_Closing;
        Closed += Window_Closed;
    }

    /// <summary>
    /// Окно ещё можно безопасно обновлять? После Close() IsLoaded становится
    /// false, а IsVisible — false ещё до закрытия (окно скрыто), поэтому обе
    /// проверки обязательны для любого обращения к UI из фонового потока.
    /// </summary>
    private bool IsUiAlive => !_isClosing && IsLoaded && IsVisible;

    /// <summary>Отменяет текущее сканирование, если оно идёт.</summary>
    private void CancelActiveScan()
    {
        CancellationTokenSource? cts = _scanCts;
        if (cts is null) return;

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Источник уже освобождён — отменять нечего.
        }
    }

    /// <summary>
    /// Освобождает источник отмены конкретного сканирования. Обращения к уже
    /// освобождённому CancellationToken (IsCancellationRequested,
    /// ThrowIfCancellationRequested) безопасны, поэтому гонка с «догоняющим»
    /// фоновым Task.Run ничего не ломает.
    /// </summary>
    private void ReleaseScanCts(CancellationTokenSource cts)
    {
        if (ReferenceEquals(_scanCts, cts))
        {
            _scanCts = null;
        }

        try
        {
            cts.Dispose();
        }
        catch
        {
            // Ничего критичного: освобождение источника отмены.
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _isClosing = true;
        CancelActiveScan();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _isClosing = true;
        CancelActiveScan();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();

        // Закрытие во время сканирования: гасим CTS заранее, чтобы фоновый
        // Task.Run не продолжал обход диска "в пустоту".
        _isClosing = true;
        CancelActiveScan();

        Close();
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        string initial = Directory.Exists(TargetFolderBox.Text) ? TargetFolderBox.Text : "C:\\";
        var folder = CyberFolderPickerDialog.PickFolder(this, "ВЫБОР ПАПКИ ДЛЯ АНАЛИЗА WIZTREE", initial);
        if (!string.IsNullOrEmpty(folder))
        {
            TargetFolderBox.Text = folder;
        }
    }

    private async void StartScan_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();

        if (_isScanning)
        {
            CancelActiveScan();
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

        var cts = new CancellationTokenSource();
        _scanCts = cts;
        var ct = cts.Token;

        var allFiles = new List<WizFileInfo>();

        // Троттлинг прогресса: раньше InvokeAsync вызывался на КАЖДЫЙ каталог.
        // При сканировании C:\ это десятки тысяч задач в очередь Dispatcher,
        // каждая с интерполяцией строки и полным layout-проходом — UI вставал
        // на всё время сканирования, и кнопку «Прервать» было не нажать.
        long lastUiUpdateTicks = 0;
        const long UiUpdateIntervalTicks = 100_000; // ~10 Гц (100 мс)

        try
        {
            var sw = Stopwatch.StartNew();
            var rootNode = await Task.Run(() => ScanDirectoryTree(root, allFiles, ct, p =>
            {
                // Фоновый поток: после отмены (в т.ч. из-за закрытия окна)
                // очередь Dispatcher больше не пополняем.
                if (ct.IsCancellationRequested) return;

                long now = Environment.TickCount64;
                if (now - lastUiUpdateTicks < UiUpdateIntervalTicks) return;
                lastUiUpdateTicks = now;

                // Helpers.UiDispatcher.Post — расширение, а НЕ Dispatcher.Post.
                // Оно само проверяет HasShutdownStarted, поэтому при выходе из
                // приложения fire-and-forget вызов не роняет процесс.
                Dispatcher.Post(() =>
                {
                    // Повторная проверка: между постановкой в очередь и её
                    // обработкой окно могло закрыться.
                    if (ct.IsCancellationRequested || !IsUiAlive) return;
                    StatusText.Text = $"Сканирование: {p}";
                });
            }), ct);

            sw.Stop();

            // Пользователь мог нажать «Прервать» или закрыть окно — UI больше не трогаем.
            if (ct.IsCancellationRequested || !IsUiAlive)
            {
                return;
            }

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
            if (IsUiAlive)
            {
                StatusText.Text = "Сканирование прервано пользователем.";
            }
        }
        catch (Exception ex)
        {
            if (IsUiAlive)
            {
                StatusText.Text = $"Ошибка: {ex.Message}";
            }
        }
        finally
        {
            // finally тоже выполняется для закрытого окна (await на отменённом
            // Task.Run бросает OperationCanceledException немедленно, пока
            // фоновый обход ещё сворачивается). Поэтому состояние кнопок
            // восстанавливаем только у живого окна.
            if (IsUiAlive)
            {
                _isScanning = false;
                ScanBtn.Content = "⚡ Начать сканирование";
                ScanProgressBar.Visibility = Visibility.Collapsed;
            }

            ReleaseScanCts(cts);
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
                catch (OperationCanceledException)
                {
                    // Отмена — пробрасываем. Раньше bare catch проглатывал её,
                    // из-за чего кнопка «Прервать» не останавливала сканирование.
                    throw;
                }
                catch
                {
                    // Игнорируем только недоступные подкаталоги.
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Игнорируем только недоступные каталоги (нет прав, отсоединён диск).
        }

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
