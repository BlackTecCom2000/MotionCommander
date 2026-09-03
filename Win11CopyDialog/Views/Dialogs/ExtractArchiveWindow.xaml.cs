using System.IO;
using System.Windows;
using Win11CopyDialog.Helpers;
using Win11CopyDialog.Modules.ArchiveEngine.Models;
using Win11CopyDialog.Modules.ArchiveEngine.Services;

namespace Win11CopyDialog.Views.Dialogs;

public partial class ExtractArchiveWindow : Window
{
    private readonly string _archivePath;
    private readonly List<string>? _specificEntries;
    private CancellationTokenSource? _cts;

    public ExtractArchiveWindow(string archivePath, List<string>? specificEntries = null)
    {
        InitializeComponent();
        _archivePath = archivePath;
        _specificEntries = specificEntries;

        ArchiveNameText.Text = Path.GetFileName(archivePath);
        string parent = Path.GetDirectoryName(archivePath) ?? "";
        DestFolderBox.Text = parent;

        BackdropHelper.Apply(this, Models.ThemeManager.Instance.Backdrop, Models.ThemeManager.Instance.IsDark);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Выберите папку для распаковки",
            InitialDirectory = DestFolderBox.Text
        };
        if (dialog.ShowDialog() == true)
        {
            DestFolderBox.Text = dialog.FolderName;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        _cts?.Cancel();
        Close();
    }

    private async void TestArchive_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        TestBtn.IsEnabled = false;
        VisualCard.Visibility = Visibility.Visible;
        StatusText.Text = "Проверка целостности архива…";

        _cts = new CancellationTokenSource();
        var progress = new Progress<ArchiveProgress>(p =>
        {
            ExtractVis.Progress = p.ProgressPercent;
            ExtractProgress.Value = p.ProgressPercent;
            ExtractFileText.Text = $"🛡 Проверка: {p.CurrentFile}";
            StatusText.Text = $"{Formatters.Bytes(p.BytesProcessed)} / {Formatters.Bytes(p.TotalBytes)}";
        });

        try
        {
            bool ok = await ArchiveService.TestArchiveIntegrityAsync(_archivePath, PasswordInput.Password, progress, _cts.Token);
            HapticAudio.PlaySuccess();
            if (ok)
            {
                StatusText.Text = "Архив полностью исправен!";
                MessageBox.Show("Архив проверен. Ошибок CRC и повреждений не обнаружено.", "Проверка успешна", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                StatusText.Text = "Обнаружены ошибки в архиве.";
                MessageBox.Show("Внимание! Обнаружены ошибки целостности или неверный пароль.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка проверки: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            TestBtn.IsEnabled = true;
        }
    }

    private async void StartExtract_Click(object sender, RoutedEventArgs e)
    {
        string target = DestFolderBox.Text.Trim();
        if (string.IsNullOrEmpty(target))
        {
            MessageBox.Show("Укажите папку назначения.", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (CreateSubfolderCheck.IsChecked == true)
        {
            string sub = Path.GetFileNameWithoutExtension(_archivePath);
            target = Path.Combine(target, sub);
        }

        HapticAudio.PlayClick();
        ExtractBtn.IsEnabled = false;
        TestBtn.IsEnabled = false;
        VisualCard.Visibility = Visibility.Visible;
        StatusText.Text = "Распаковка файлов…";

        _cts = new CancellationTokenSource();
        var progress = new Progress<ArchiveProgress>(p =>
        {
            ExtractVis.Progress = p.ProgressPercent;
            ExtractProgress.Value = p.ProgressPercent;
            ExtractFileText.Text = $"📦 {p.CurrentFile}";
            StatusText.Text = $"{Formatters.Bytes(p.BytesProcessed)} / {Formatters.Bytes(p.TotalBytes)} • {Formatters.Speed(p.CurrentSpeedBytesPerSec)}";
        });

        try
        {
            bool overwrite = OverwriteCheck.IsChecked == true;
            await ArchiveService.ExtractAsync(_archivePath, target, _specificEntries, PasswordInput.Password, overwrite, progress, _cts.Token);
            HapticAudio.PlaySuccess();
            StatusText.Text = "Распаковка завершена!";
            MessageBox.Show($"Файлы успешно распакованы в:\n{target}", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Операция отменена.";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка распаковки: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ExtractBtn.IsEnabled = true;
            TestBtn.IsEnabled = true;
        }
    }
}
