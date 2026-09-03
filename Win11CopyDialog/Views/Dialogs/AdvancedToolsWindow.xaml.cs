using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Win11CopyDialog.Helpers;
using Win11CopyDialog.Modules.AdvancedTools.ChecksumEngine;
using Win11CopyDialog.Modules.FileManager.Services;

namespace Win11CopyDialog.Views.Dialogs;

public partial class AdvancedToolsWindow : Window
{
    private ChecksumResult? _lastResult;

    public AdvancedToolsWindow(string? initialPath = null)
    {
        InitializeComponent();
        BackdropHelper.Apply(this, Models.ThemeManager.Instance.Backdrop, Models.ThemeManager.Instance.IsDark);

        if (!string.IsNullOrEmpty(initialPath))
        {
            if (File.Exists(initialPath))
            {
                HashFilePathBox.Text = initialPath;
                File1Box.Text = initialPath;
                ComputeHashesFor(initialPath);
            }
            else if (Directory.Exists(initialPath))
            {
                AnalyzeFolderBox.Text = initialPath;
            }
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        Close();
    }

    // ---------- ХЭШИ ----------

    private void SelectHashFile_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        var ofd = new OpenFileDialog { Title = "Выберите файл для вычисления хэшей" };
        if (ofd.ShowDialog() == true)
        {
            HashFilePathBox.Text = ofd.FileName;
            ComputeHashesFor(ofd.FileName);
        }
    }

    private async void ComputeHashesFor(string filePath)
    {
        HashProgress.Visibility = Visibility.Visible;
        var prog = new Progress<double>(p => HashProgress.Value = p);

        try
        {
            _lastResult = await ChecksumService.ComputeHashesAsync(filePath, prog);
            HapticAudio.PlaySuccess();

            CrcBox.Text = _lastResult.Crc32;
            Md5Box.Text = _lastResult.Md5;
            Sha256Box.Text = _lastResult.Sha256;
            Sha512Box.Text = _lastResult.Sha512;

            CheckMatch();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка вычисления хэш-сумм: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            HashProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void CopyCrc_Click(object sender, RoutedEventArgs e) { HapticAudio.PlayClick(); Clipboard.SetText(CrcBox.Text); }
    private void CopyMd5_Click(object sender, RoutedEventArgs e) { HapticAudio.PlayClick(); Clipboard.SetText(Md5Box.Text); }
    private void CopySha256_Click(object sender, RoutedEventArgs e) { HapticAudio.PlayClick(); Clipboard.SetText(Sha256Box.Text); }
    private void CopySha512_Click(object sender, RoutedEventArgs e) { HapticAudio.PlayClick(); Clipboard.SetText(Sha512Box.Text); }

    private void ExpectedHashBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        CheckMatch();
    }

    private void CheckMatch()
    {
        if (_lastResult == null) return;
        string expected = ExpectedHashBox.Text.Trim();
        if (string.IsNullOrEmpty(expected))
        {
            HashMatchText.Text = "";
            return;
        }

        bool matchCrc = string.Equals(expected, _lastResult.Crc32, StringComparison.OrdinalIgnoreCase);
        bool matchMd5 = string.Equals(expected, _lastResult.Md5, StringComparison.OrdinalIgnoreCase);
        bool matchSha256 = string.Equals(expected, _lastResult.Sha256, StringComparison.OrdinalIgnoreCase);
        bool matchSha512 = string.Equals(expected, _lastResult.Sha512, StringComparison.OrdinalIgnoreCase);

        if (matchSha256)
        {
            HashMatchText.Text = "✓ Хэш полностью совпадает (SHA-256)";
            HashMatchText.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
        }
        else if (matchMd5)
        {
            HashMatchText.Text = "✓ Хэш полностью совпадает (MD5)";
            HashMatchText.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
        }
        else if (matchCrc)
        {
            HashMatchText.Text = "✓ Хэш полностью совпадает (CRC32)";
            HashMatchText.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
        }
        else if (matchSha512)
        {
            HashMatchText.Text = "✓ Хэш полностью совпадает (SHA-512)";
            HashMatchText.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
        }
        else
        {
            HashMatchText.Text = "✕ Хэш-сумма НЕ совпадает с вычисленными значениями";
            HashMatchText.Foreground = new SolidColorBrush(Color.FromRgb(220, 38, 38));
        }
    }

    // ---------- СРАВНЕНИЕ ----------

    private void SelectFile1_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        var ofd = new OpenFileDialog { Title = "Выберите первый файл" };
        if (ofd.ShowDialog() == true) File1Box.Text = ofd.FileName;
    }

    private void SelectFile2_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        var ofd = new OpenFileDialog { Title = "Выберите второй файл" };
        if (ofd.ShowDialog() == true) File2Box.Text = ofd.FileName;
    }

    private async void CompareFiles_Click(object sender, RoutedEventArgs e)
    {
        string p1 = File1Box.Text.Trim();
        string p2 = File2Box.Text.Trim();
        if (string.IsNullOrEmpty(p1) || string.IsNullOrEmpty(p2) || !File.Exists(p1) || !File.Exists(p2))
        {
            MessageBox.Show("Укажите оба существующих файла для сравнения.", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        HapticAudio.PlayClick();
        CompareBtn.IsEnabled = false;
        CompareProgress.Visibility = Visibility.Visible;
        CompareResultText.Text = "Выполняется побайтовое бинарное сравнение…";

        var prog = new Progress<double>(p => CompareProgress.Value = p);

        try
        {
            var (equal, offset, msg) = await ChecksumService.CompareBinaryAsync(p1, p2, prog);
            HapticAudio.PlaySuccess();

            if (equal)
            {
                CompareResultText.Text = $"✓ {msg}";
                CompareResultText.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            }
            else
            {
                CompareResultText.Text = $"✕ Файлы различаются!\n{msg}";
                CompareResultText.Foreground = new SolidColorBrush(Color.FromRgb(220, 38, 38));
            }
        }
        catch (Exception ex)
        {
            CompareResultText.Text = $"Ошибка: {ex.Message}";
        }
        finally
        {
            CompareBtn.IsEnabled = true;
            CompareProgress.Visibility = Visibility.Collapsed;
        }
    }

    // ---------- АНАЛИЗАТОР ПАПКИ ----------

    private void SelectFolderAnalyze_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        var dialog = new OpenFolderDialog { Title = "Выберите папку для анализа" };
        if (dialog.ShowDialog() == true)
        {
            AnalyzeFolderBox.Text = dialog.FolderName;
        }
    }

    private async void StartAnalyze_Click(object sender, RoutedEventArgs e)
    {
        string path = AnalyzeFolderBox.Text.Trim();
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            MessageBox.Show("Укажите существующую папку для анализа.", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        HapticAudio.PlayClick();
        StartAnalyzeBtn.IsEnabled = false;
        AnalyzeProgress.Visibility = Visibility.Visible;
        AnalyzeResultText.Text = "Идёт глубокое сканирование…";
        AnalyzeDetailText.Text = "Подсчёт объёма файлов и подкаталогов…";

        var prog = new Progress<long>(b =>
        {
            AnalyzeResultText.Text = $"Размер: {Formatters.Bytes(b)}";
        });

        try
        {
            long total = await FileSystemService.CalculateDirectorySizeAsync(path, prog);
            HapticAudio.PlaySuccess();
            AnalyzeResultText.Text = $"Полный размер: {Formatters.Bytes(total)} ({total:N0} байт)";
            AnalyzeDetailText.Text = $"Путь: {path}";
        }
        catch (Exception ex)
        {
            AnalyzeResultText.Text = $"Ошибка: {ex.Message}";
        }
        finally
        {
            StartAnalyzeBtn.IsEnabled = true;
            AnalyzeProgress.Visibility = Visibility.Collapsed;
        }
    }
}
