using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Win11CopyDialog.Helpers;
using Win11CopyDialog.Models;

namespace Win11CopyDialog.Views.Dialogs;

public sealed class AppConfigData
{
    public string Theme { get; set; } = "MicaDark";
    public string Accent { get; set; } = "Неон Циан";
    public string Backdrop { get; set; } = "Mica";
    public bool HapticAudioEnabled { get; set; } = true;
    public int DefaultBufferSizeKb { get; set; } = 1024;
    public int ConcurrencyThreads { get; set; } = 4;
    public bool DirectIoBypassCache { get; set; } = false;
    public bool SequentialScanOptimized { get; set; } = true;
    public bool AutoVerifyCrc32 { get; set; } = true;
    public string RegisteredUser { get; set; } = "BlackTecCom - Jaborov Daler";
    public string Version { get; set; } = "3.0.0 Pro";
}

public partial class SettingsWindow : Window
{
    private readonly string _configFilePath;
    private bool _initializing = true;

    public SettingsWindow()
    {
        InitializeComponent();
        ThemeManager.Instance.Apply();
        BackdropHelper.Apply(this, ThemeManager.Instance.Backdrop, ThemeManager.Instance.IsDark);

        string appDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MotionCommander");
        Directory.CreateDirectory(appDir);
        _configFilePath = Path.Combine(appDir, "appsettings.json");

        InitThemes();
        InitAccents();
        InitHaptics();
        LoadConfigToEditor();

        _initializing = false;
    }

    private void InitThemes()
    {
        ThemesComboBox.Items.Clear();
        foreach (AppTheme t in Enum.GetValues<AppTheme>())
        {
            ThemesComboBox.Items.Add(ThemeManager.Instance.ThemeDisplayName(t));
        }

        ThemesComboBox.SelectedIndex = (int)ThemeManager.Instance.Theme;
        UpdateThemeDescription(ThemeManager.Instance.Theme);
    }

    private void ThemesComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        HapticAudio.PlayClick();

        if (ThemesComboBox.SelectedIndex >= 0)
        {
            var selectedTheme = (AppTheme)ThemesComboBox.SelectedIndex;
            ThemeManager.Instance.Theme = selectedTheme;
            BackdropHelper.Apply(this, ThemeManager.Instance.Backdrop, ThemeManager.Instance.IsDark);
            UpdateThemeDescription(selectedTheme);
            SaveCurrentStateToConfig();
        }
    }

    private void UpdateThemeDescription(AppTheme t)
    {
        ThemeDescriptionText.Text = t switch
        {
            AppTheme.CyberpunkDark => "⚡ Глубокий тёмный индиго #0B0E14 с неоновым акцентом и высокой контрастностью.",
            AppTheme.OledMidnight => "🌑 Абсолютный чёрный цвет #000000 для экономии энергии на OLED матрицах и бесконечной глубины.",
            AppTheme.MatrixEmerald => "💻 Стиль терминала кибер-хакеров: тёмно-зелёные карточки и изумрудный неоновый луч.",
            AppTheme.SunsetAmber => "🔥 Тёплые угольные тона #14100E в гармонии с сияющим янтарным и золотым свечением.",
            AppTheme.RoyalIndigo => "🔮 Премиальный глубокий сапфировый ультрамарин с фиолетовыми переливами.",
            AppTheme.MicaDark => "◈ Фирменный полупрозрачный материал Windows 11 Mica Alt в тёмном исполнении.",
            AppTheme.MicaLight => "◈ Светлый воздушный матовый стиль Windows 11 Mica с мягкими тенями.",
            AppTheme.Acrylic => "⬣ Глубокий эффект матового стекла Acrylic с адаптивным шумом DWM.",
            AppTheme.Dark => "☾ Классический чистый тёмный интерфейс без прозрачностей.",
            AppTheme.Light => "☀ Чистый минималистичный светлый стиль Windows.",
            _ => "Индивидуальный стиль оформления."
        };
    }

    private void InitAccents()
    {
        AccentsWrapPanel.Children.Clear();
        foreach (var acc in ThemeManager.Instance.Accents)
        {
            var btn = new Button
            {
                Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(8, 4, 8, 4),
                Style = (Style)FindResource("CyberToolButton"),
                Tag = acc
            };

            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var preview = new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(7),
                Background = acc.IsSystem ? (Brush)FindResource("AccentBrush") : acc.Brush,
                Margin = new Thickness(0, 0, 6, 0),
                BorderBrush = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)),
                BorderThickness = new Thickness(1)
            };
            var txt = new TextBlock
            {
                Text = acc.Name,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            sp.Children.Add(preview);
            sp.Children.Add(txt);
            btn.Content = sp;

            btn.Click += (s, _) =>
            {
                HapticAudio.PlayClick();
                if (s is Button b && b.Tag is AccentOption opt)
                {
                    ThemeManager.Instance.Accent = opt;
                    SaveCurrentStateToConfig();
                }
            };

            AccentsWrapPanel.Children.Add(btn);
        }
    }

    private void BackdropRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        HapticAudio.PlayClick();

        BackdropType bType = BackdropType.None;
        if (BackdropMicaRadio.IsChecked == true) bType = BackdropType.MicaAlt;
        else if (BackdropAcrylicRadio.IsChecked == true) bType = BackdropType.Acrylic;

        if (Application.Current != null)
        {
            foreach (Window w in Application.Current.Windows)
            {
                BackdropHelper.Apply(w, bType, ThemeManager.Instance.IsDark);
            }
        }
        SaveCurrentStateToConfig();
    }

    private void InitHaptics()
    {
        HapticsEnabledCheckBox.IsChecked = HapticAudio.Enabled;
    }

    private void HapticsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        HapticAudio.Enabled = HapticsEnabledCheckBox.IsChecked == true;
        HapticAudio.PlayClick();
        SaveCurrentStateToConfig();
    }

    private void TestClick_Click(object sender, RoutedEventArgs e) => HapticAudio.PlayClick();
    private void TestHover_Click(object sender, RoutedEventArgs e) => HapticAudio.PlayHover();
    private void TestSuccess_Click(object sender, RoutedEventArgs e) => HapticAudio.PlaySuccess();
    private void TestScroll_Click(object sender, RoutedEventArgs e) => HapticAudio.PlayScrollTick();

    private void LoadConfigToEditor()
    {
        try
        {
            if (!File.Exists(_configFilePath))
            {
                SaveCurrentStateToConfig();
            }

            string json = File.ReadAllText(_configFilePath);
            ConfigEditorBox.Text = json;
        }
        catch (Exception ex)
        {
            ConfigEditorBox.Text = $"// Ошибка чтения конфига: {ex.Message}";
        }
    }

    private void SaveCurrentStateToConfig()
    {
        try
        {
            var data = new AppConfigData
            {
                Theme = ThemeManager.Instance.Theme.ToString(),
                Accent = ThemeManager.Instance.Accent.Name,
                Backdrop = BackdropMicaRadio?.IsChecked == true ? "MicaAlt" : (BackdropAcrylicRadio?.IsChecked == true ? "Acrylic" : "Solid"),
                HapticAudioEnabled = HapticAudio.Enabled,
                DefaultBufferSizeKb = DefaultBufferCombo?.SelectedIndex switch
                {
                    0 => 256,
                    1 => 512,
                    2 => 1024,
                    3 => 2048,
                    4 => 4096,
                    5 => 8192,
                    _ => 1024
                },
                ConcurrencyThreads = ThreadsCombo?.SelectedIndex switch
                {
                    0 => 1,
                    1 => 2,
                    2 => 4,
                    3 => 8,
                    4 => 16,
                    _ => 4
                },
                DirectIoBypassCache = DirectIoCheck?.IsChecked == true,
                SequentialScanOptimized = SequentialScanCheck?.IsChecked == true,
                AutoVerifyCrc32 = VerifyCrcCheck?.IsChecked == true
            };

            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(data, options);
            File.WriteAllText(_configFilePath, json);
            if (ConfigEditorBox != null)
            {
                ConfigEditorBox.Text = json;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to save config: {ex.Message}");
        }
    }

    private void ReloadConfig_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        LoadConfigToEditor();
        StatusMessage.Text = "Конфигурация перезагружена с диска.";
    }

    private void ResetConfig_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        var data = new AppConfigData();
        var options = new JsonSerializerOptions { WriteIndented = true };
        ConfigEditorBox.Text = JsonSerializer.Serialize(data, options);
        File.WriteAllText(_configFilePath, ConfigEditorBox.Text);
        StatusMessage.Text = "Настройки сброшены к значениям по умолчанию.";
    }

    private void SaveConfig_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        try
        {
            string raw = ConfigEditorBox.Text;
            using var doc = JsonDocument.Parse(raw); // Проверка валидности JSON
            File.WriteAllText(_configFilePath, raw);
            StatusMessage.Text = "Конфигурация успешно сохранена и проверена.";
            MessageBox.Show("Конфигурация JSON сохранена на диск!", "Успешно", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusMessage.Text = $"Ошибка синтаксиса JSON: {ex.Message}";
            MessageBox.Show($"Ошибка в структуре JSON:\n{ex.Message}", "Ошибка синтаксиса", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenConfigFolder_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        string dir = Path.GetDirectoryName(_configFilePath) ?? "";
        if (Directory.Exists(dir))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
        }
    }

    private void CopyAlif_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        Clipboard.SetText("4444888810226013");
        StatusMessage.Text = "Номер карты Alif VISA скопирован в буфер обмена!";
        MessageBox.Show("Номер карты Alif VISA (4444 8888 1022 6013) скопирован в буфер обмена!\nСпасибо за поддержку разработки!", "Донат скопирован", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void CopyDc_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        Clipboard.SetText("4713380021651431");
        StatusMessage.Text = "Номер карты DC Bank VISA скопирован в буфер обмена!";
        MessageBox.Show("Номер карты DC Bank VISA (4713 3800 2165 1431) скопирован в буфер обмена!\nСпасибо за поддержку разработки!", "Донат скопирован", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        Close();
    }
}
