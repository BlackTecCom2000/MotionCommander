using System.IO;
using System.Windows;
using Microsoft.Win32;
using Win11CopyDialog.Helpers;
using Win11CopyDialog.Modules.ArchiveEngine.Models;
using Win11CopyDialog.Modules.ArchiveEngine.Services;

namespace Win11CopyDialog.Views.Dialogs;

public partial class CreateArchiveWindow : Window
{
    private readonly List<string> _sourcePaths;
    private CancellationTokenSource? _cts;

    public CreateArchiveWindow(IEnumerable<string> sourcePaths)
    {
        InitializeComponent();
        _sourcePaths = sourcePaths.ToList();

        string first = _sourcePaths.FirstOrDefault() ?? "";
        string dir = File.Exists(first) ? Path.GetDirectoryName(first) ?? ""
            : Directory.Exists(first) ? Path.GetDirectoryName(first) ?? "" : "";
        string baseName = Path.GetFileNameWithoutExtension(first);
        if (string.IsNullOrEmpty(baseName)) baseName = "Archive";

        ArchivePathBox.Text = Path.Combine(dir, $"{baseName}.zip");
        BackdropHelper.Apply(this, Models.ThemeManager.Instance.Backdrop, Models.ThemeManager.Instance.IsDark);
    }

    private void LevelSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LevelCombo != null)
        {
            LevelCombo.SelectedIndex = (int)Math.Round(e.NewValue);
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        var sfd = new SaveFileDialog
        {
            Title = "Сохранить архив как",
            Filter = "ZIP архив (*.zip)|*.zip|7Z архив (*.7z)|*.7z|TAR архив (*.tar)|*.tar|Все файлы (*.*)|*.*",
            FileName = Path.GetFileName(ArchivePathBox.Text)
        };
        if (sfd.ShowDialog() == true)
        {
            ArchivePathBox.Text = sfd.FileName;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        _cts?.Cancel();
        Close();
    }

    private async void StartCompress_Click(object sender, RoutedEventArgs e)
    {
        string dest = ArchivePathBox.Text.Trim();
        if (string.IsNullOrEmpty(dest))
        {
            MessageBox.Show("Укажите путь для сохранения архива.", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        HapticAudio.PlayClick();
        StartBtn.IsEnabled = false;
        VisualCard.Visibility = Visibility.Visible;
        StatusText.Text = "Сжатие файлов…";

        var format = FormatCombo.SelectedIndex switch
        {
            1 => ArchiveFormat.SevenZip,
            2 => ArchiveFormat.Tar,
            3 => ArchiveFormat.TarGz,
            4 => ArchiveFormat.TarBz2,
            _ => ArchiveFormat.Zip
        };

        var level = (CompressionLevelPreset)Math.Clamp(LevelCombo.SelectedIndex, 0, 4);
        string password = PasswordInput.Password;

        _cts = new CancellationTokenSource();
        var progress = new Progress<ArchiveProgress>(p =>
        {
            CompVis.Progress = p.ProgressPercent;
            CompVis.Ratio = p.RatioPercent;
            CompProgress.Value = p.ProgressPercent;
            CompFileText.Text = $"📄 {p.CurrentFile}";
            CompRatioText.Text = $"Коэффициент: {p.RatioPercent:0.0}% (сэкономлено {p.SavedPercent:0.0}%)";
            StatusText.Text = $"{Formatters.Bytes(p.BytesProcessed)} / {Formatters.Bytes(p.TotalBytes)} • {Formatters.Speed(p.CurrentSpeedBytesPerSec)}";
        });

        try
        {
            await ArchiveService.CompressAsync(_sourcePaths, dest, format, level, password, progress, _cts.Token);
            HapticAudio.PlaySuccess();
            StatusText.Text = "Архив успешно создан!";
            MessageBox.Show($"Архив успешно создан:\n{dest}", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Операция отменена.";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка создания архива: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            StartBtn.IsEnabled = true;
        }
    }
}
