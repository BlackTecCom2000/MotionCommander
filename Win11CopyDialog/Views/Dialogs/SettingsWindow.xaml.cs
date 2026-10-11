using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Win11CopyDialog.Helpers;
using Win11CopyDialog.Models;

namespace Win11CopyDialog.Views.Dialogs;

public sealed class AppConfigData
{
    // ---------- Оформление ----------
    public string Theme { get; set; } = nameof(AppTheme.MotionGlass);
    public string Accent { get; set; } = "Неон Циан";
    public string Backdrop { get; set; } = "";          // пусто = следовать теме
    public double BackdropIntensity { get; set; } = 0.65; // 0.0 .. 1.0 (яркость аур и фона)
    public bool HapticAudioEnabled { get; set; } = true;
    public bool ClickSoundsEnabled { get; set; } = true;
    public bool HoverSoundsEnabled { get; set; } = true;
    public bool ScrollHapticEnabled { get; set; } = true;
    public bool SuccessSoundsEnabled { get; set; } = true;

    /// <summary>Режим анимаций: 0 = Эконом, 1 = Максимум.</summary>
    public int AnimationQuality { get; set; } = 1;
    public bool LiveBackdropEnabled { get; set; } = true;

    // ---------- Портативный режим ----------
    /// <summary>true = хранить настройки рядом с программой. null = определять автоматически.</summary>
    public bool? PortableMode { get; set; }

    // ---------- Прокрутка ----------
    public bool SmoothScrollEnabled { get; set; } = true;
    public double ScrollDampingRate { get; set; } = 22.0;   // 10..38
    public double ScrollStepSize { get; set; } = 110.0;      // 50..220
    public bool ScrollInertiaEnabled { get; set; } = true;
    public string ScrollPreset { get; set; } = "Balanced";  // UltraSilk/Balanced/Snappy/Custom

    // ---------- Визуальные эффекты ----------
    public bool TabAnimationsEnabled { get; set; } = true;
    public bool NeonGlowEnabled { get; set; } = true;
    public bool HapticSoundsEnabled { get; set; } = true;

    // ---------- Параметры I/O ----------
    public int DefaultBufferSizeKb { get; set; } = 1024;
    public int ConcurrencyThreads { get; set; } = 4;
    public bool DirectIoBypassCache { get; set; }
    public bool SequentialScanOptimized { get; set; } = true;
    public bool AutoVerifyCrc32 { get; set; } = true;

    /// <summary>Версия приложения, записавшая конфиг.</summary>
    public string Version { get; set; } = "";

    /// <summary>Общий экземпляр. Читается всеми потребителями (SmoothScroll, темы, окна).</summary>
    public static AppConfigData Instance { get; private set; } = LoadFromFile(AppPaths.SettingsFile);

    /// <summary>Перезачитывает конфиг с диска (после смены режима хранения или внешнего редактирования).</summary>
    public static void Reload() => Instance = LoadFromFile(AppPaths.SettingsFile);

    /// <summary>
    /// Сохраняет конфиг. Пишет атомарно (через временный файл), иначе сбой
    /// питания в середине записи оставлял бы повреждённый settings.json.
    /// </summary>
    public void Save()
    {
        try
        {
            DefaultBufferSizeKb = Math.Clamp(DefaultBufferSizeKb, 256, 8192);
            ConcurrencyThreads = Math.Clamp(ConcurrencyThreads, 1, 16);
            BackdropIntensity = Math.Clamp(BackdropIntensity, 0.0, 1.0);
            ScrollDampingRate = Math.Clamp(ScrollDampingRate, 10, 38);
            ScrollStepSize = Math.Clamp(ScrollStepSize, 50, 220);
            Version = "";

            string path = AppPaths.SettingsFile;
            AppPaths.EnsureDir(Path.GetDirectoryName(path) ?? AppPaths.WritableDataDirectory);

            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(path)) File.Replace(tmp, path, null, ignoreMetadataErrors: true);
            else File.Move(tmp, path);
        }
        catch
        {
            // Настройки — не критичные данные: отказ записи не должен ломать UI.
        }
    }

    /// <summary>
    /// Читает конфигурацию с диска с безопасными значениями по умолчанию.
    /// </summary>
    public static AppConfigData LoadFromFile(string path)
    {
        var defaults = new AppConfigData();

        try
        {
            if (!File.Exists(path)) return defaults;

            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return defaults;

            var loaded = JsonSerializer.Deserialize<AppConfigData>(json);
            if (loaded == null) return defaults;

            // Валидация диапазонов
            loaded.DefaultBufferSizeKb = Math.Clamp(loaded.DefaultBufferSizeKb, 256, 8192);
            loaded.ConcurrencyThreads = Math.Clamp(loaded.ConcurrencyThreads, 1, 16);
            loaded.BackdropIntensity = Math.Clamp(loaded.BackdropIntensity > 0 ? loaded.BackdropIntensity : 0.65, 0.0, 1.0);
            loaded.ScrollDampingRate = Math.Clamp(loaded.ScrollDampingRate > 0 ? loaded.ScrollDampingRate : 22.0, 10.0, 38.0);
            loaded.ScrollStepSize = Math.Clamp(loaded.ScrollStepSize > 0 ? loaded.ScrollStepSize : 110.0, 50.0, 220.0);
            loaded.Version = "";

            return loaded;
        }
        catch
        {
            return defaults;
        }
    }
}

public partial class SettingsWindow : Window
{
    private readonly string _configFilePath;
    private bool _initializing = true;
    private string _originalLoadedJson = "";
    private bool _isEditingRawConfig = false;

    /// <summary>
    /// Путь к файлу конфигурации. Всегда использует централизованный AppPaths.SettingsFile.
    /// </summary>
    public static string ConfigFilePath => AppPaths.SettingsFile;

    public SettingsWindow()
    {
        InitializeComponent();
        ThemeManager.Instance.Apply();
        BackdropHelper.Apply(this, ThemeManager.Instance.Backdrop, ThemeManager.Instance.IsDark);

        _configFilePath = ConfigFilePath;

        try
        {
            string? appDir = Path.GetDirectoryName(_configFilePath);
            if (!string.IsNullOrEmpty(appDir)) Directory.CreateDirectory(appDir);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Не удалось подготовить папку конфигурации:\n{ex.Message}\n\n" +
                "Настройки могут не сохраняться. Проверьте права доступа.",
                "Ошибка конфигурации", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        InitThemes();
        InitAccents();
        InitBackdrop();
        InitBackdropIntensity();
        InitAnimationQuality();
        InitInstallMode();
        InitHaptics();
        InitIoSettings();
        UpdateIoSettingsSummary();
        InitShellIntegration();
        LoadConfigToEditor();
        InitVersionDisplay();

        _initializing = false;
    }

    // ================= Перетаскивание и заголовок окна =================

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Min_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        Close();
    }

    private void InitVersionDisplay()
    {
        string currentVer = Modules.UpdateEngine.UpdateService.GetCurrentVersion();
        if (HeaderVersionBadgeText != null) HeaderVersionBadgeText.Text = $"v{currentVer}";
        if (SettingsCurrentVersionText != null) SettingsCurrentVersionText.Text = $"v{currentVer}";
        if (LicenseVersionText != null) LicenseVersionText.Text = $"Motion Commander Pro v{currentVer} — Зарегистрировано на:";
    }

    private void OpenVersionSelector_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        var dlg = new VersionSelectDialog
        {
            Owner = this
        };
        dlg.ShowDialog();
        InitVersionDisplay();
    }

    // ================= Интеграция с Windows =================

    private void InitShellIntegration()
    {
        UpdateExplorerStatus();
        UpdateContextMenuStatus();
    }

    private void UpdateExplorerStatus()
    {
        bool isReplaced = Modules.WindowsShellIntegration.ShellIntegrationService.IsExplorerReplaced();
        if (isReplaced)
        {
            ExplorerStatusBadgeText.Text = "⚡ Motion Commander (По умолчанию)";
            ExplorerStatusBadgeText.Foreground = (Brush)FindResource("AccentBrush");
            ToggleExplorerBtn.Content = "✔ Motion Commander активен";
            ToggleExplorerBtn.IsEnabled = false;
            RestoreExplorerBtn.IsEnabled = true;
        }
        else
        {
            ExplorerStatusBadgeText.Text = "Стандартный Windows Explorer";
            ExplorerStatusBadgeText.Foreground = (Brush)FindResource("TextSecondaryBrush");
            ToggleExplorerBtn.Content = "⚡ Назначить проводником по умолчанию";
            ToggleExplorerBtn.IsEnabled = true;
            RestoreExplorerBtn.IsEnabled = false;
        }
    }

    private void UpdateContextMenuStatus()
    {
        bool isIntegrated = Modules.WindowsShellIntegration.ShellIntegrationService.IsIntegrated();
        if (isIntegrated)
        {
            ContextMenuStatusBadgeText.Text = "✔ Интегрировано в Windows";
            ContextMenuStatusBadgeText.Foreground = (Brush)FindResource("AccentBrush");
            ToggleContextMenuBtn.IsEnabled = false;
            RemoveContextMenuBtn.IsEnabled = true;
        }
        else
        {
            ContextMenuStatusBadgeText.Text = "Не интегрировано";
            ContextMenuStatusBadgeText.Foreground = (Brush)FindResource("TextSecondaryBrush");
            ToggleContextMenuBtn.IsEnabled = true;
            RemoveContextMenuBtn.IsEnabled = false;
        }
    }

    private void ToggleExplorerReplacement_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();

        var confirm = MessageBox.Show(
            "Вы хотите назначить Motion Commander основным файловым менеджером Windows по умолчанию?\n\n" +
            "• При открытии папок и локальных дисков будет открываться Motion Commander вместо Проводника.\n" +
            "• Рабочий стол, панель задач и меню «Пуск» продолжат работать штатно.\n" +
            "• Вы сможете в любой момент вернуть стандартный Проводник нажатием одной кнопки.\n\n" +
            "Применить изменения?",
            "Замена Проводника Windows",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        if (Modules.WindowsShellIntegration.ShellIntegrationService.SetExplorerReplacement(true, out string error))
        {
            UpdateExplorerStatus();
            StatusMessage.Text = "Motion Commander успешно назначен проводником Windows по умолчанию.";
            HapticAudio.PlaySuccess();
            MessageBox.Show(
                "Motion Commander успешно назначен проводником по умолчанию!\n\n" +
                "Теперь открытие папок и дисков будет происходить в Motion Commander.",
                "Замена Проводника", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            StatusMessage.Text = $"Ошибка назначения проводника: {error}";
            MessageBox.Show($"Не удалось изменить ассоциации проводника:\n\n{error}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RestoreExplorer_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();

        var confirm = MessageBox.Show(
            "Восстановить стандартный Проводник Windows (explorer.exe) для открытия папок и дисков?",
            "Возврат стандартного Проводника",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        if (Modules.WindowsShellIntegration.ShellIntegrationService.SetExplorerReplacement(false, out string error))
        {
            UpdateExplorerStatus();
            StatusMessage.Text = "Стандартный Windows Explorer успешно возвращен по умолчанию.";
            HapticAudio.PlaySuccess();
            MessageBox.Show(
                "Стандартный Проводник Windows успешно восстановлен по умолчанию!",
                "Возврат Проводника", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            StatusMessage.Text = $"Ошибка восстановления: {error}";
            MessageBox.Show($"Не удалось восстановить стандартный проводник:\n\n{error}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ToggleContextMenu_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();

        if (Modules.WindowsShellIntegration.ShellIntegrationService.SetIntegration(true, out string error))
        {
            UpdateContextMenuStatus();
            StatusMessage.Text = "Пункты контекстного меню успешно добавлены.";
            HapticAudio.PlaySuccess();
        }
        else
        {
            MessageBox.Show($"Ошибка интеграции контекстного меню:\n\n{error}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RemoveContextMenu_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();

        if (Modules.WindowsShellIntegration.ShellIntegrationService.SetIntegration(false, out string error))
        {
            UpdateContextMenuStatus();
            StatusMessage.Text = "Пункты контекстного меню успешно удалены.";
            HapticAudio.PlaySuccess();
        }
        else
        {
            MessageBox.Show($"Ошибка удаления из контекстного меню:\n\n{error}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ================= Темы и акценты =================

    private static readonly AppTheme[] ThemeOrder =
    {
        AppTheme.MotionGlass,
        AppTheme.CosmicNebula,
        AppTheme.DeepSea,
        AppTheme.CyberpunkDark,
        AppTheme.RoyalIndigo,
        AppTheme.SunsetAmber,
        AppTheme.MicaDark,
        AppTheme.Dark,
        AppTheme.OledMidnight,
        AppTheme.MatrixEmerald,
        AppTheme.TerminalAmber,
        AppTheme.RoseQuartz,
        AppTheme.MicaLight,
        AppTheme.Acrylic,
        AppTheme.Light,
        AppTheme.MinimalWhite,
    };

    private void InitThemes()
    {
        ThemesComboBox.Items.Clear();
        foreach (var t in ThemeOrder)
            ThemesComboBox.Items.Add(ThemeManager.Instance.ThemeDisplayName(t));

        int idx = Array.IndexOf(ThemeOrder, ThemeManager.Instance.Theme);
        ThemesComboBox.SelectedIndex = idx >= 0 ? idx : 0;
        UpdateThemeDescription(ThemeManager.Instance.Theme);
    }

    private void ThemesComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        HapticAudio.PlayClick();

        int i = ThemesComboBox.SelectedIndex;
        if (i < 0 || i >= ThemeOrder.Length) return;

        var selectedTheme = ThemeOrder[i];
        ThemeManager.Instance.Theme = selectedTheme;
        ThemeManager.Instance.BackdropOverride = null;
        BackdropHelper.Apply(this, ThemeManager.Instance.Backdrop, ThemeManager.Instance.IsDark);
        UpdateThemeDescription(selectedTheme);
        SaveCurrentStateToConfig();
    }

    private void UpdateThemeDescription(AppTheme t)
    {
        ThemeDescriptionText.Text = t switch
        {
            AppTheme.MotionGlass => "✨ Motion Commander Glass OS: фирменная полупрозрачная кибер-эстетика с эффектами Liquid Glass и акцентным неоном.",
            AppTheme.CosmicNebula => "🌌 Cosmic Nebula: глубокий индиго #04050F, фиолетовые туманности и живой звёздный фон с планетами.",
            AppTheme.DeepSea => "🌊 Deep Sea: холодная глубина океана с бирюзовыми тонами и спокойным фоновым градиентом.",
            AppTheme.CyberpunkDark => "⚡ Cyberpunk Dark: глубокий тёмный индиго #0B0E14 с неоновым акцентом и максимальным контрастом.",
            AppTheme.OledMidnight => "🌑 OLED Midnight: абсолютный чёрный #000000 для экономии энергии на OLED-панелях. Без размытия.",
            AppTheme.MatrixEmerald => "💻 Matrix Emerald: стиль терминала хакера, тёмно-зелёные карточки, изумрудный неон.",
            AppTheme.TerminalAmber => "🖥 Terminal Amber: янтарный фосфор на глубоком чёрном фоне. Ретро-терминал высокой чёткости.",
            AppTheme.SunsetAmber => "🔥 Sunset Amber: тёплые угольные тона #14100E с янтарным и золотым свечением элементов.",
            AppTheme.RoyalIndigo => "🔮 Royal Indigo: глубокий сапфировый ультрамарин с фиолетовыми переливами.",
            AppTheme.MicaDark => "◈ Windows 11 Mica Dark: полупрозрачный материал Mica Alt в тёмном исполнении.",
            AppTheme.MicaLight => "◈ Windows 11 Mica Light: воздушный матовый стиль Windows 11 Mica со светлыми поверхностями.",
            AppTheme.Acrylic => "⬣ Fluent Acrylic: эффект матового стекла с глубоким аппаратным размытием DWM.",
            AppTheme.RoseQuartz => "🌸 Rose Quartz: светлая пастельная тема: розовый кварц, мягкие тени и высокая читаемость.",
            AppTheme.Dark => "☾ Windows Тёмная: классический строгий тёмный интерфейс без прозрачности.",
            AppTheme.Light => "☀ Windows Светлая: классический светлый интерфейс высокой чёткости.",
            AppTheme.MinimalWhite => "⬜ Minimal White: абсолютно плоский минималистичный белый дизайн.",
            _ => "Стиль оформления интерфейса."
        };

        if (LiveBackdropHint != null)
        {
            LiveBackdropHint.Text = ThemeManager.Instance.LiveBackdropEnabled
                ? "🌌 Живой фон включён — звёзды и туманности анимируются"
                : "Живой фон выключен";
        }
    }

    private void InitAccents()
    {
        AccentsWrapPanel.Children.Clear();
        foreach (var acc in ThemeManager.Instance.Accents)
        {
            bool isCurrent = string.Equals(ThemeManager.Instance.Accent.Name, acc.Name, StringComparison.OrdinalIgnoreCase);

            var btn = new Button
            {
                Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(10, 5, 10, 5),
                Style = (Style)FindResource("GlassButton"),
                Tag = acc
            };

            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var preview = new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(7),
                Background = acc.IsSystem ? (Brush)FindResource("AccentBrush") : acc.Brush,
                Margin = new Thickness(0, 0, 7, 0),
                BorderBrush = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
                BorderThickness = new Thickness(1)
            };
            var txt = new TextBlock
            {
                Text = acc.Name,
                FontSize = 11,
                FontWeight = isCurrent ? FontWeights.Bold : FontWeights.Normal,
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
                    InitAccents();
                    SaveCurrentStateToConfig();
                }
            };

            AccentsWrapPanel.Children.Add(btn);
        }
    }

    private void InitBackdrop()
    {
        var b = ThemeManager.Instance.Backdrop;
        if (b == BackdropType.Acrylic) BackdropAcrylicRadio.IsChecked = true;
        else if (b == BackdropType.None) BackdropSolidRadio.IsChecked = true;
        else BackdropMicaRadio.IsChecked = true;
    }

    private void BackdropRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        HapticAudio.PlayClick();

        BackdropType bType = BackdropType.None;
        if (BackdropMicaRadio.IsChecked == true) bType = BackdropType.MicaAlt;
        else if (BackdropAcrylicRadio.IsChecked == true) bType = BackdropType.Acrylic;

        ThemeManager.Instance.BackdropOverride = bType == BackdropType.None ? null : bType;
        SaveCurrentStateToConfig();
    }

    private void InitBackdropIntensity()
    {
        double val = ThemeManager.Instance.BackdropIntensity * 100.0;
        BackdropIntensitySlider.Value = Math.Clamp(val, 0, 100);
        UpdateBackdropIntensityLabel(BackdropIntensitySlider.Value);
        SmoothScrollCheck.IsChecked = AppConfigData.Instance.SmoothScrollEnabled;
    }

    private void BackdropIntensitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        UpdateBackdropIntensityLabel(e.NewValue);
        ThemeManager.Instance.BackdropIntensity = e.NewValue / 100.0;
        SaveCurrentStateToConfig();
    }

    private void UpdateBackdropIntensityLabel(double val)
    {
        if (BackdropIntensityText == null) return;
        int pct = (int)Math.Round(val);
        BackdropIntensityText.Text = $"{pct}%";
    }

    private void SmoothScrollCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        HapticAudio.PlayClick();
        SaveCurrentStateToConfig();
    }

    private void LiveBackdrop_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        HapticAudio.PlayClick();

        ThemeManager.Instance.LiveBackdropEnabled = LiveBackdropCheck?.IsChecked == true;
        UpdateAnimationPerfText();
        UpdateThemeDescription(ThemeManager.Instance.Theme);
        SaveCurrentStateToConfig();
    }

    // ================= Качество анимаций =================

    private void InitAnimationQuality()
    {
        bool max = ThemeManager.Instance.AnimationQuality == AnimationQuality.Maximum;
        AnimationMaximumRadio.IsChecked = max;
        AnimationEconomyRadio.IsChecked = !max;
        LiveBackdropCheck.IsChecked = ThemeManager.Instance.LiveBackdropEnabled;
        UpdateAnimationPerfText();
    }

    private void AnimationQuality_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        HapticAudio.PlayClick();

        ThemeManager.Instance.AnimationQuality = AnimationMaximumRadio?.IsChecked == true
            ? AnimationQuality.Maximum
            : AnimationQuality.Economy;

        if (ThemeManager.Instance.AnimationQuality == AnimationQuality.Economy && LiveBackdropCheck != null)
            LiveBackdropCheck.IsChecked = false;

        UpdateAnimationPerfText();
        UpdateThemeDescription(ThemeManager.Instance.Theme);
        SaveCurrentStateToConfig();
    }

    private void UpdateAnimationPerfText()
    {
        if (AnimationPerfText == null) return;

        if (!ThemeManager.AnimationsAllowed)
        {
            AnimationPerfText.Text = "⚠ Анимации отключены в системных настройках Windows. Приложение уважает эту настройку.";
            return;
        }

        bool live = ThemeManager.Instance.LiveBackdropEnabled;
        AnimationPerfText.Text = ThemeManager.Instance.AnimationQuality == AnimationQuality.Maximum
            ? (live
                ? "Режим «Высокая плавность»: 60 FPS, живой фон активен, интеллектуальная пауза при неактивном окне."
                : "Режим «Высокая плавность»: 60 FPS, живой фон отключен — фон статичен, анимации интерфейса работают.")
            : "Режим «Эконом»: 30 FPS, живой фон и визуальные эффекты выключены. Минимальная нагрузка на CPU и батарею.";
    }

    // ================= Режим установки =================

    private void InitInstallMode()
    {
        InstallModeAutoRadio.IsChecked = true;
        UpdateInstallModeDetail();
    }

    private void InstallMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        HapticAudio.PlayClick();

        bool? wanted = InstallModePortableRadio?.IsChecked == true ? true
            : InstallModeInstalledRadio?.IsChecked == true ? false
            : null;

        if (wanted is bool p)
        {
            if (!AppPaths.SetPortable(p, out string? err))
            {
                MessageBox.Show(err ?? "Не удалось изменить режим хранения.", "Режим установки",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                InstallModeAutoRadio.IsChecked = true;
                UpdateInstallModeDetail();
                return;
            }
            AppPaths.ResetCaches();
            AppConfigData.Reload();
        }

        UpdateInstallModeDetail();
        SaveCurrentStateToConfig();
    }

    private void UpdateInstallModeDetail()
    {
        if (InstallModeDetailText == null) return;

        bool writable = AppPaths.IsDirectoryWritable(AppPaths.WritableDataDirectory);
        InstallModeDetailText.Text =
            AppPaths.Describe() +
            (writable ? "" : "\n\n⚠ Каталог данных защищён от записи. Настройки могут не сохраняться.");
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        try
        {
            AppPaths.EnsureDir(AppPaths.WritableDataDirectory);
            var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            psi.ArgumentList.Add(AppPaths.WritableDataDirectory);
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            if (StatusMessage != null) StatusMessage.Text = $"Не удалось открыть папку: {ex.Message}";
        }
    }

    // ================= Звук и Haptics =================

    private void InitHaptics()
    {
        var cfg = AppConfigData.Instance;
        HapticsEnabledCheckBox.IsChecked = HapticAudio.Enabled;
        ClickSoundsCheck.IsChecked = HapticAudio.ClickEnabled;
        HoverSoundsCheck.IsChecked = HapticAudio.HoverEnabled;
        ScrollSoundsCheck.IsChecked = HapticAudio.ScrollTickEnabled;
        SuccessSoundsCheck.IsChecked = HapticAudio.SuccessEnabled;
    }

    private void HapticsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        HapticAudio.Enabled = HapticsEnabledCheckBox.IsChecked == true;
        HapticAudio.PlayClick();
        SaveCurrentStateToConfig();
    }

    private void SoundSubToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        HapticAudio.ClickEnabled = ClickSoundsCheck.IsChecked == true;
        HapticAudio.HoverEnabled = HoverSoundsCheck.IsChecked == true;
        HapticAudio.ScrollTickEnabled = ScrollSoundsCheck.IsChecked == true;
        HapticAudio.SuccessEnabled = SuccessSoundsCheck.IsChecked == true;
        HapticAudio.PlayClick();
        SaveCurrentStateToConfig();
    }

    private void TestClick_Click(object sender, RoutedEventArgs e) => HapticAudio.PlayClick();
    private void TestHover_Click(object sender, RoutedEventArgs e) => HapticAudio.PlayHover();
    private void TestScroll_Click(object sender, RoutedEventArgs e) => HapticAudio.PlayScrollTick();
    private void TestSuccess_Click(object sender, RoutedEventArgs e) => HapticAudio.PlaySuccess();

    // ================= Параметры ввода-вывода (I/O) =================

    private void InitIoSettings()
    {
        var cfg = AppConfigData.Instance;

        DefaultBufferCombo.SelectedIndex = cfg.DefaultBufferSizeKb switch
        {
            <= 256 => 0,
            <= 512 => 1,
            <= 1024 => 2,
            <= 2048 => 3,
            <= 4096 => 4,
            _ => 5
        };

        ThreadsCombo.SelectedIndex = cfg.ConcurrencyThreads switch
        {
            <= 1 => 0,
            <= 2 => 1,
            <= 4 => 2,
            <= 8 => 3,
            _ => 4
        };

        DirectIoCheck.IsChecked = cfg.DirectIoBypassCache;
        SequentialScanCheck.IsChecked = cfg.SequentialScanOptimized;
        VerifyCrcCheck.IsChecked = cfg.AutoVerifyCrc32;
    }

    private void UpdateIoSettingsSummary()
    {
        if (IoSettingsSummaryText == null) return;

        int sector = IoSettings.SectorSize;
        int bufKb = DefaultBufferCombo?.SelectedIndex switch
        {
            0 => 256, 1 => 512, 2 => 1024, 3 => 2048, 4 => 4096, 5 => 8192, _ => 1024
        };
        int threads = ThreadsCombo?.SelectedIndex switch
        {
            0 => 1, 1 => 2, 2 => 4, 3 => 8, 4 => 16, _ => 4
        };
        bool directIo = DirectIoCheck?.IsChecked == true;
        bool seq = SequentialScanCheck?.IsChecked == true;
        bool crc = VerifyCrcCheck?.IsChecked == true;

        var sb = new System.Text.StringBuilder();
        sb.Append($"• Размер I/O буфера: {bufKb} КБ");
        sb.Append($"\n• Потоков копирования: {threads}");
        sb.Append($"\n• Последовательное чтение (Sequential Scan): {(seq ? "активно" : "выключено")}");

        if (directIo)
        {
            bool aligned = (bufKb * 1024) % sector == 0;
            sb.Append(aligned
                ? $"\n• Обход кэша записи (Direct I/O): включён (кратен сектору {sector} байт)"
                : $"\n• Обход кэша записи (Direct I/O): выключен — буфер {bufKb} КБ не выровнен по сектору {sector} байт");
        }
        else
        {
            sb.Append("\n• Обход кэша записи (Direct I/O): выключен (кэширование Windows активно)");
        }

        sb.Append(crc
            ? "\n• CRC-32 проверка на лету: контрольная сумма сверяется побайтно для каждого файла"
            : "\n• CRC-32 проверка на лету: выключена (сверяется только размер файлов)");

        IoSettingsSummaryText.Text = sb.ToString();
    }

    private void IoSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        UpdateIoSettingsSummary();
        SaveCurrentStateToConfig();
    }

    // ================= Редактор конфигурации =================

    private void LoadConfigToEditor()
    {
        try
        {
            if (!File.Exists(_configFilePath))
            {
                SaveCurrentStateToConfig();
            }

            string json = File.ReadAllText(_configFilePath);
            _originalLoadedJson = json;
            ConfigEditorBox.Text = json;
            ConfigErrorBanner.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ConfigEditorBox.Text = $"// Ошибка чтения конфига: {ex.Message}";
        }
    }

    private void ConfigEditorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initializing) return;
        _isEditingRawConfig = true;
    }

    private void ValidateSyntax_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        ValidateJson(showSuccessDialog: true);
    }

    private bool ValidateJson(bool showSuccessDialog = false)
    {
        try
        {
            string raw = ConfigEditorBox.Text;
            using var doc = JsonDocument.Parse(raw);
            var parsed = JsonSerializer.Deserialize<AppConfigData>(raw);
            if (parsed == null) throw new JsonException("Не удалось разобрать корневую структуру конфига.");

            ConfigErrorBanner.Visibility = Visibility.Collapsed;
            StatusMessage.Text = "Синтаксис JSON корректен и валиден.";
            HapticAudio.PlaySuccess();

            if (showSuccessDialog)
            {
                MessageBox.Show("Синтаксис JSON полностью валиден и соответствует схеме конфигурации!",
                    "Проверка синтаксиса", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return true;
        }
        catch (JsonException jex)
        {
            ConfigErrorBanner.Visibility = Visibility.Visible;
            string errLoc = jex.LineNumber.HasValue
                ? $"Строка {jex.LineNumber.Value + 1}, позиция {jex.BytePositionInLine ?? 0}"
                : "Неизвестная позиция";

            ConfigErrorText.Text = $"Синтаксическая ошибка JSON [{errLoc}]:\n{jex.Message}";
            StatusMessage.Text = $"Ошибка синтаксиса: {errLoc}";

            if (jex.LineNumber.HasValue && jex.LineNumber.Value >= 0 && jex.LineNumber.Value < ConfigEditorBox.LineCount)
            {
                int charIdx = ConfigEditorBox.GetCharacterIndexFromLineIndex((int)jex.LineNumber.Value);
                if (charIdx >= 0)
                {
                    ConfigEditorBox.Focus();
                    ConfigEditorBox.Select(charIdx, 0);
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            ConfigErrorBanner.Visibility = Visibility.Visible;
            ConfigErrorText.Text = $"Ошибка структуры JSON:\n{ex.Message}";
            StatusMessage.Text = $"Ошибка: {ex.Message}";
            return false;
        }
    }

    private void RevertConfig_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        ConfigEditorBox.Text = _originalLoadedJson;
        _isEditingRawConfig = false;
        ConfigErrorBanner.Visibility = Visibility.Collapsed;
        StatusMessage.Text = "Изменения отменены. Восстановлен сохранённый текст файла.";
    }

    private void ReloadConfig_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        LoadConfigToEditor();
        _isEditingRawConfig = false;
        StatusMessage.Text = "Конфигурация перезагружена с диска.";
    }

    private void SaveConfig_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();

        if (!ValidateJson(showSuccessDialog: false))
        {
            MessageBox.Show("Невозможно сохранить конфигурацию с синтаксическими ошибками. Исправьте ошибки перед сохранением.",
                "Ошибка синтаксиса JSON", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        try
        {
            string raw = ConfigEditorBox.Text;
            string tmp = _configFilePath + ".tmp";
            File.WriteAllText(tmp, raw);
            if (File.Exists(_configFilePath)) File.Replace(tmp, _configFilePath, null, ignoreMetadataErrors: true);
            else File.Move(tmp, _configFilePath);

            _originalLoadedJson = raw;
            _isEditingRawConfig = false;

            AppConfigData.Reload();
            ApplyConfigToApp(AppConfigData.Instance);

            // Синхронизируем UI с новыми значениями
            _initializing = true;
            InitThemes();
            InitAccents();
            InitBackdrop();
            InitBackdropIntensity();
            InitAnimationQuality();
            InitInstallMode();
            InitHaptics();
            InitIoSettings();
            UpdateIoSettingsSummary();
            _initializing = false;

            StatusMessage.Text = "Конфигурация сохранена и успешно применена.";
            HapticAudio.PlaySuccess();
            MessageBox.Show("Конфигурация JSON сохранена на диск и успешно применена ко всем окнам!", "Успешно", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusMessage.Text = $"Не удалось сохранить: {ex.Message}";
            MessageBox.Show($"Ошибка записи файла конфигурации:\n{ex.Message}", "Ошибка записи", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ResetConfig_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();

        var res = MessageBox.Show(
            "Сбросить файл конфигурации settings.json к стандартным значениям по умолчанию?",
            "Сброс файла конфигурации",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (res != MessageBoxResult.Yes) return;

        try
        {
            var data = new AppConfigData();
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(data, options);

            File.WriteAllText(_configFilePath, json);
            _originalLoadedJson = json;
            ConfigEditorBox.Text = json;
            _isEditingRawConfig = false;
            ConfigErrorBanner.Visibility = Visibility.Collapsed;

            ApplyConfigToApp(data);
            data.Save();

            _initializing = true;
            InitThemes();
            InitAccents();
            InitBackdrop();
            InitBackdropIntensity();
            InitAnimationQuality();
            InitInstallMode();
            InitHaptics();
            InitIoSettings();
            UpdateIoSettingsSummary();
            _initializing = false;

            StatusMessage.Text = "Конфигурация сброшена до значений по умолчанию.";
            HapticAudio.PlaySuccess();
        }
        catch (Exception ex)
        {
            StatusMessage.Text = $"Не удалось сбросить конфиг: {ex.Message}";
            MessageBox.Show($"Ошибка сброса:\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ResetAllSettings_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();

        var res = MessageBox.Show(
            "Сбросить все параметры Motion Commander к значениям по умолчанию?\n\n" +
            "• Тема оформления, цвет и фон будут возвращены к стандартным\n" +
            "• Параметры звука и Haptics будут включены\n" +
            "• Параметры I/O будут возвращены к оптимальным значениям (1 МБ буфер, 4 потока)\n\n" +
            "Продолжить сброс всех настроек?",
            "Сброс всех настроек",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (res != MessageBoxResult.Yes) return;

        try
        {
            var data = new AppConfigData();
            data.Save();
            AppConfigData.Reload();
            ApplyConfigToApp(AppConfigData.Instance);

            _initializing = true;
            InitThemes();
            InitAccents();
            InitBackdrop();
            InitBackdropIntensity();
            InitAnimationQuality();
            InitInstallMode();
            InitHaptics();
            InitIoSettings();
            UpdateIoSettingsSummary();
            LoadConfigToEditor();
            _isEditingRawConfig = false;
            _initializing = false;

            StatusMessage.Text = "Все настройки сброшены к значениям по умолчанию.";
            HapticAudio.PlaySuccess();
            MessageBox.Show("Все настройки успешно сброшены к значениям по умолчанию!", "Сброс завершён", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusMessage.Text = $"Не удалось выполнить сброс: {ex.Message}";
            MessageBox.Show($"Сброс не выполнен:\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenConfigFolder_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        string dir = Path.GetDirectoryName(_configFilePath) ?? "";
        if (!Directory.Exists(dir))
        {
            StatusMessage.Text = "Папка конфигурации не найдена.";
            return;
        }

        try
        {
            var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            psi.ArgumentList.Add(dir);
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            StatusMessage.Text = $"Не удалось открыть папку: {ex.Message}";
        }
    }

    // ================= Сохранение и применение настроек =================

    private void SaveCurrentStateToConfig()
    {
        if (_initializing) return;

        try
        {
            var data = AppConfigData.Instance;

            data.Theme = ThemeManager.Instance.Theme.ToString();
            data.Accent = ThemeManager.Instance.Accent.IsSystem ? "" : ThemeManager.Instance.Accent.Name;
            data.Backdrop = BackdropMicaRadio?.IsChecked == true ? "MicaAlt"
                : (BackdropAcrylicRadio?.IsChecked == true ? "Acrylic" : (BackdropSolidRadio?.IsChecked == true ? "None" : ""));
            data.BackdropIntensity = BackdropIntensitySlider != null ? BackdropIntensitySlider.Value / 100.0 : 0.65;
            data.AnimationQuality = (int)ThemeManager.Instance.AnimationQuality;
            data.LiveBackdropEnabled = ThemeManager.Instance.LiveBackdropEnabled;
            data.SmoothScrollEnabled = SmoothScrollCheck?.IsChecked == true;

            data.HapticAudioEnabled = HapticsEnabledCheckBox?.IsChecked == true;
            data.ClickSoundsEnabled = ClickSoundsCheck?.IsChecked == true;
            data.HoverSoundsEnabled = HoverSoundsCheck?.IsChecked == true;
            data.ScrollHapticEnabled = ScrollSoundsCheck?.IsChecked == true;
            data.SuccessSoundsEnabled = SuccessSoundsCheck?.IsChecked == true;

            data.DefaultBufferSizeKb = DefaultBufferCombo?.SelectedIndex switch
            {
                0 => 256,
                1 => 512,
                2 => 1024,
                3 => 2048,
                4 => 4096,
                5 => 8192,
                _ => 1024
            };
            data.ConcurrencyThreads = ThreadsCombo?.SelectedIndex switch
            {
                0 => 1,
                1 => 2,
                2 => 4,
                3 => 8,
                4 => 16,
                _ => 4
            };
            data.DirectIoBypassCache = DirectIoCheck?.IsChecked == true;
            data.SequentialScanOptimized = SequentialScanCheck?.IsChecked == true;
            data.AutoVerifyCrc32 = VerifyCrcCheck?.IsChecked == true;

            data.Save();
            ThemeManager.Instance.Save();

            if (ConfigEditorBox != null && !_isEditingRawConfig)
            {
                _originalLoadedJson = File.Exists(_configFilePath)
                    ? File.ReadAllText(_configFilePath)
                    : JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                ConfigEditorBox.Text = _originalLoadedJson;
            }

            if (StatusMessage != null) StatusMessage.Text = "Параметры сохранены и применены в реальном времени.";
        }
        catch (Exception ex)
        {
            if (StatusMessage != null)
            {
                StatusMessage.Text = $"Ошибка сохранения настроек: {ex.Message}";
            }
        }
    }

    /// <summary>
    /// Применяет конфигурацию к живому приложению (темы, акценты, звуки, параметры I/O).
    /// </summary>
    public static void ApplyConfigToApp(AppConfigData data)
    {
        if (data is null) return;

        if (Enum.TryParse<AppTheme>(data.Theme, ignoreCase: true, out var theme)
            && Enum.IsDefined(typeof(AppTheme), theme))
        {
            ThemeManager.Instance.Theme = theme;
        }

        if (!string.IsNullOrWhiteSpace(data.Accent))
        {
            var acc = ThemeManager.Instance.Accents
                .FirstOrDefault(a => string.Equals(a.Name, data.Accent, StringComparison.OrdinalIgnoreCase));
            if (acc != null) ThemeManager.Instance.Accent = acc;
        }

        ThemeManager.Instance.BackdropOverride =
            !string.IsNullOrWhiteSpace(data.Backdrop) &&
            Enum.TryParse<BackdropType>(data.Backdrop, ignoreCase: true, out var bd) && bd != BackdropType.None
                ? bd
                : null;

        ThemeManager.Instance.BackdropIntensity = data.BackdropIntensity > 0 ? data.BackdropIntensity : 0.65;

        ThemeManager.Instance.AnimationQuality =
            Enum.IsDefined(typeof(AnimationQuality), data.AnimationQuality)
                ? (AnimationQuality)data.AnimationQuality
                : AnimationQuality.Maximum;

        ThemeManager.Instance.LiveBackdropEnabled = data.LiveBackdropEnabled;

        HapticAudio.Enabled = data.HapticAudioEnabled;
        HapticAudio.ClickEnabled = data.ClickSoundsEnabled;
        HapticAudio.HoverEnabled = data.HoverSoundsEnabled;
        HapticAudio.ScrollTickEnabled = data.ScrollHapticEnabled;
        HapticAudio.SuccessEnabled = data.SuccessSoundsEnabled;
    }

    // ================= Донаты и поддержка =================

    private void CopyDonation_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();

        if (sender is not FrameworkElement { Tag: string tag } || !DonationInfo.TryParseTag(tag, out var method))
        {
            StatusMessage.Text = "Не удалось определить карту для копирования.";
            return;
        }

        string status = DonationInfo.CopyToClipboard(method, out bool success);
        StatusMessage.Text = status;

        MessageBox.Show(
            status,
            "Поддержка проекта",
            MessageBoxButton.OK,
            success ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }
}
