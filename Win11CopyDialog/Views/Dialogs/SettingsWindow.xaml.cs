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
    // ---------- Оформление ----------
    public string Theme { get; set; } = nameof(AppTheme.MotionGlass);
    public string Accent { get; set; } = "Неон Циан";
    public string Backdrop { get; set; } = "";          // пусто = следовать теме
    public bool HapticAudioEnabled { get; set; } = true;

    /// <summary>Режим анимаций: 0 = Эконом, 1 = Максимум.</summary>
    public int AnimationQuality { get; set; } = 1;
    public bool LiveBackdropEnabled { get; set; } = true;

    // ---------- Портативный режим ----------
    /// <summary>true = хранить настройки рядом с программой. null = определять автоматически.</summary>
    public bool? PortableMode { get; set; }

    // ---------- Прокрутка (ранее отдельный AppSettings конфликтовал за один файл) ----------
    public bool SmoothScrollEnabled { get; set; } = true;
    public double ScrollDampingRate { get; set; } = 22.0;   // 10..38
    public double ScrollStepSize { get; set; } = 110.0;      // 50..220
    public bool ScrollInertiaEnabled { get; set; } = true;
    public bool ScrollHapticEnabled { get; set; }
    public string ScrollPreset { get; set; } = "Balanced";  // UltraSilk/Balanced/Snappy/Custom

    // ---------- Визуальные эффекты ----------
    public bool TabAnimationsEnabled { get; set; } = true;
    public bool NeonGlowEnabled { get; set; } = true;
    public bool HapticSoundsEnabled { get; set; } = true;

    // ---------- Параметры I/O (реально читаются движками) ----------
    public int DefaultBufferSizeKb { get; set; } = 1024;
    public int ConcurrencyThreads { get; set; } = 4;
    public bool DirectIoBypassCache { get; set; }
    public bool SequentialScanOptimized { get; set; } = true;
    public bool AutoVerifyCrc32 { get; set; } = true;

    /// <summary>
    /// Версия приложения, записавшая конфиг. Раньше здесь жёстко стояло
    /// «3.0.0 Pro», и это значение попадало в конфиг каждого пользователя.
    /// </summary>
    public string Version { get; set; } = "";

    /// <summary>Общий экземпляр. Читается всеми потребителями (SmoothScroll, темы, окна).</summary>
    public static AppConfigData Instance { get; private set; } = LoadFromFile(AppPaths.SettingsFile);

    /// <summary>Перезачитывает конфиг с диска (после смены режима хранения).</summary>
    public static void Reload() => Instance = LoadFromFile(AppPaths.SettingsFile);

    /// <summary>
    /// Сохраняет конфиг. Пишет атомарно (через временный файл), иначе сбой
    /// питания в середине записи оставлял бы повреждённый settings.json
    /// и все настройки молча сбрасывались бы при следующем запуске.
    /// </summary>
    public void Save()
    {
        try
        {
            DefaultBufferSizeKb = Math.Clamp(DefaultBufferSizeKb, 256, 8192);
            ConcurrencyThreads = Math.Clamp(ConcurrencyThreads, 1, 16);
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
    /// Читает конфигурацию с диска.
    ///
    /// <para>Раньше AppConfigData создавался и сериализовался, но НИКОГДА не
    /// десериализовался: ни одна настройка не переживала перезапуск. Теперь
    /// метод читает файл, проверяет структуру и возвращает заполненный объект
    /// с безопасными значениями по умолчанию при любой ошибке.</para>
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

            // Валидация: не доверяем значениям из файла.
            loaded.DefaultBufferSizeKb = Math.Clamp(loaded.DefaultBufferSizeKb, 256, 8192);
            loaded.ConcurrencyThreads = Math.Clamp(loaded.ConcurrencyThreads, 1, 16);
            loaded.Version = "";

            return loaded;
        }
        catch
        {
            // Повреждённый конфиг не должен мешать запуску приложения.
            return defaults;
        }
    }
}

public partial class SettingsWindow : Window
{
    private readonly string _configFilePath;
    private bool _initializing = true;

    /// <summary>
    /// Путь к файлу конфигурации. ОБЯЗАТЕЛЬНО должен совпадать с путём,
    /// который читается при старте в App.xaml.cs, иначе настройки снова
    /// окажутся «потерянными» (пишется в один файл, читается из другого).
    /// </summary>
    public static string ConfigFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MotionCommander",
        "settings.json");

    public SettingsWindow()
    {
        InitializeComponent();
        ThemeManager.Instance.Apply();
        BackdropHelper.Apply(this, ThemeManager.Instance.Backdrop, ThemeManager.Instance.IsDark);

        _configFilePath = ConfigFilePath;

        // Раньше Directory.CreateDirectory стоял в конструкторе БЕЗ try/catch.
        // При отказе в доступе исключение вылетало из new SettingsWindow(), и
        // окно настроек становилось невозможно открыть вообще.
        try
        {
            string? appDir = Path.GetDirectoryName(_configFilePath);
            if (!string.IsNullOrEmpty(appDir)) Directory.CreateDirectory(appDir);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Не удалось подготовить папку конфигурации:\n{ex.Message}\n\n" +
                "Настройки не смогут сохраняться. Проверьте права доступа.",
                "Ошибка конфигурации", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        InitThemes();
        InitAccents();
        InitAnimationQuality();
        InitInstallMode();
        InitHaptics();
        InitShellIntegration();
        LoadConfigToEditor();
        InitVersionDisplay();

        _initializing = false;
    }

    private void InitVersionDisplay()
    {
        string currentVer = Modules.UpdateEngine.UpdateService.GetCurrentVersion();
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
            ExplorerStatusBadgeText.Foreground = (Brush)FindResource("SecondaryTextBrush");
            ToggleExplorerBtn.Content = "⚡ Сделать Motion Commander проводником по умолчанию";
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
            ContextMenuStatusBadgeText.Foreground = (Brush)FindResource("SecondaryTextBrush");
            ToggleContextMenuBtn.IsEnabled = true;
            RemoveContextMenuBtn.IsEnabled = false;
        }
    }

    private void ToggleExplorerReplacement_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        if (Modules.WindowsShellIntegration.ShellIntegrationService.SetExplorerReplacement(true, out string error))
        {
            UpdateExplorerStatus();
            StatusMessage.Text = "Motion Commander назначен основным файловым менеджером Windows.";
            MessageBox.Show("Motion Commander успешно назначен проводником по умолчанию!\n\nТеперь открытие папок и дисков будет происходить в Motion Commander.",
                "Замена Проводника", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            StatusMessage.Text = $"Ошибка назначения проводника: {error}";
            MessageBox.Show($"Не удалось изменить ассоциации проводника:\n{error}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RestoreExplorer_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        if (Modules.WindowsShellIntegration.ShellIntegrationService.SetExplorerReplacement(false, out string error))
        {
            UpdateExplorerStatus();
            StatusMessage.Text = "Стандартный Windows Explorer успешно возвращен по умолчанию.";
            MessageBox.Show("Стандартный Проводник Windows успешно восстановлен по умолчанию!",
                "Возврат Проводника", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            StatusMessage.Text = $"Ошибка восстановления: {error}";
            MessageBox.Show($"Не удалось восстановить стандартный проводник:\n{error}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ToggleContextMenu_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        if (Modules.WindowsShellIntegration.ShellIntegrationService.SetIntegration(true, out string error))
        {
            UpdateContextMenuStatus();
            StatusMessage.Text = "Пункты контекстного меню успешно добавлены.";
        }
        else
        {
            MessageBox.Show($"Ошибка интеграции контекстного меню:\n{error}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RemoveContextMenu_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        if (Modules.WindowsShellIntegration.ShellIntegrationService.SetIntegration(false, out string error))
        {
            UpdateContextMenuStatus();
            StatusMessage.Text = "Пункты контекстного меню успешно удалены.";
        }
        else
        {
            MessageBox.Show($"Ошибка удаления из контекстного меню:\n{error}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Порядок тем в выпадающем списке. Раньше индекс ComboBox приравнивался к
    /// (int)AppTheme, поэтому добавление темы молча ломало сохранение:
    /// выбранная тема восстанавливалась как соседняя.
    /// Теперь список и перечисление расходятся, а поиск идёт по имени.
    /// </summary>
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

        // Находим текущую тему по имени, а не по индексу — устойчиво к перестановкам.
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
        // Сброс ручного выбора фона: тема задаёт собственный материал.
        ThemeManager.Instance.BackdropOverride = null;
        BackdropHelper.Apply(this, ThemeManager.Instance.Backdrop, ThemeManager.Instance.IsDark);
        UpdateThemeDescription(selectedTheme);
        SaveCurrentStateToConfig();
    }

    private void UpdateThemeDescription(AppTheme t)
    {
        ThemeDescriptionText.Text = t switch
        {
            AppTheme.CosmicNebula => "🌌 Глубокий космос: индиго #04050F, фиолетовые туманности и живой звёздный фон за окном. Самая насыщенная тема.",
            AppTheme.DeepSea => "🌊 Холодная глубина океана: бирюзовые тона и живой фоновый градиент. Успокаивает и не утомляет.",
            AppTheme.CyberpunkDark => "⚡ Глубокий тёмный индиго #0B0E14 с неоновым акцентом и моноширинным шрифтом.",
            AppTheme.OledMidnight => "🌑 Абсолютный чёрный #000000 для экономии энергии на OLED. Без скруглений.",
            AppTheme.MatrixEmerald => "💻 Стиль терминала: тёмно-зелёные карточки, изумрудный неон, моноширинный шрифт.",
            AppTheme.TerminalAmber => "🖥 Янтарный фосфор на почти чёрном фоне. Ретро-терминал с минимальным скруглением.",
            AppTheme.SunsetAmber => "🔥 Тёплые угольные тона #14100E с янтарным и золотым свечением.",
            AppTheme.RoyalIndigo => "🔮 Премиальный глубокий сапфировый ультрамарин с фиолетовыми переливами.",
            AppTheme.MicaDark => "◈ Фирменный полупрозрачный материал Windows 11 Mica Alt в тёмном исполнении.",
            AppTheme.MicaLight => "◈ Светлый воздушный матовый стиль Windows 11 Mica с мягкими тенями.",
            AppTheme.Acrylic => "⬣ Глубокий эффект матового стекла Acrylic с адаптивным шумом DWM.",
            AppTheme.RoseQuartz => "🌸 Светлая пастельная тема: розовый кварц с тёплыми тенями, высокая читаемость.",
            AppTheme.Dark => "☾ Классический чистый тёмный интерфейс без прозрачности.",
            AppTheme.Light => "☀ Чистый минималистичный светлый стиль Windows.",
            AppTheme.MinimalWhite => "⬜ Полностью белый, плоский и минималистичный интерфейс без AI-помощника.",
            _ => "Индивидуальный стиль оформления."
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

        ThemeManager.Instance.BackdropOverride = bType == BackdropType.None ? null : bType;
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

        // В режиме «Эконом» живой фон автоматически отключается:
        // держать 60 FPS анимации на слабой машине бессмысленно.
        if (ThemeManager.Instance.AnimationQuality == AnimationQuality.Economy && LiveBackdropCheck != null)
            LiveBackdropCheck.IsChecked = false;

        UpdateAnimationPerfText();
        UpdateThemeDescription(ThemeManager.Instance.Theme);
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

    private void UpdateAnimationPerfText()
    {
        if (AnimationPerfText == null) return;

        if (!ThemeManager.AnimationsAllowed)
        {
            AnimationPerfText.Text = "⚠ Анимации отключены в настройках Windows (Специальные возможности → Визуальные эффекты). Приложение уважает эту настройку.";
            return;
        }

        bool live = ThemeManager.Instance.LiveBackdropEnabled;
        AnimationPerfText.Text = ThemeManager.Instance.AnimationQuality == AnimationQuality.Maximum
            ? (live
                ? "Режим «Максимум»: 60 FPS, живой фон активен, 0 аллокаций в кадре."
                : "Режим «Максимум»: 60 FPS, живой фон выключен — фон статичный, анимации интерфейса работают.")
            : "Режим «Эконом»: 30 FPS, живой фон и декоративные эффекты отключены. Ниже нагрузка на процессор и батарею.";
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
                MessageBox.Show(err ?? "Не удалось изменить режим.", "Режим установки",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                // Возвращаем переключатель в «Авто», раз принудительный режим недоступен.
                InstallModeAutoRadio.IsChecked = true;
                UpdateInstallModeDetail();
                return;
            }
            // Режим сменился: переопределяем кэш путей и перечитываем конфиг
            // из нового места, иначе настройки «прыгали» между папками.
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
            (writable ? "" : "\n\n⚠ В папку данных нельзя записывать. Настройки могут не сохраняться.");
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
            // ВАЖНО: изменяем СУЩЕСТВУЮЩИЙ экземпляр, а не создаём новый объект.
            // Раньше здесь создавался новый AppConfigData, из-за чего все поля,
            // которых нет в этом окне (параметры прокрутки, портативный режим),
            // молча сбрасывались к значениям по умолчанию при каждом движении
            // ползунка в настройках.
            var data = AppConfigData.Instance;

            data.Theme = ThemeManager.Instance.Theme.ToString();
            data.Accent = ThemeManager.Instance.Accent.IsSystem ? "" : ThemeManager.Instance.Accent.Name;
            data.Backdrop = BackdropMicaRadio?.IsChecked == true ? "MicaAlt"
                : (BackdropAcrylicRadio?.IsChecked == true ? "Acrylic" : "");
            data.AnimationQuality = (int)ThemeManager.Instance.AnimationQuality;
            data.LiveBackdropEnabled = ThemeManager.Instance.LiveBackdropEnabled;
            data.HapticAudioEnabled = HapticAudio.Enabled;

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

            // Атомарная запись: при сбое питания посреди File.WriteAllText
            // settings.json оставался бы обрезанным, и все настройки пропали бы.
            data.Save();
            ThemeManager.Instance.Save();

            if (ConfigEditorBox != null)
                ConfigEditorBox.Text = File.Exists(_configFilePath)
                    ? File.ReadAllText(_configFilePath)
                    : JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });

            // Раньше ошибка сохранения уходила только в Debug.WriteLine, который
            // в Release-сборке вырезается компилятором. Пользователь не знал,
            // что настройки не сохранились.
            if (StatusMessage != null) StatusMessage.Text = "Настройки сохранены.";
        }
        catch (Exception ex)
        {
            if (StatusMessage != null)
            {
                StatusMessage.Text = $"Ошибка сохранения настроек: {ex.Message}";
            }
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

        try
        {
            var data = new AppConfigData();
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(data, options);

            File.WriteAllText(_configFilePath, json);
            ConfigEditorBox.Text = json;

            // Раньше сброс ТОЛЬКО записывал файл, но не применял значения к
            // живому приложению. Более того, следующий же SaveCurrentStateToConfig()
            // (он вызывается при любом изменении контрола) затирал «сброшенный»
            // файл текущим состоянием. Теперь настройки реально применяются.
            ApplyConfigToApp(data);

            StatusMessage.Text = "Настройки сброшены к значениям по умолчанию и применены.";
            HapticAudio.PlaySuccess();
        }
        catch (Exception ex)
        {
            StatusMessage.Text = $"Не удалось сбросить настройки: {ex.Message}";
            MessageBox.Show($"Сброс настроек не выполнен:\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Применяет конфигурацию к живому приложению.
    ///
    /// Вызывается при сбросе настроек и при загрузке конфига при старте.
    ///
    /// <para>Раньше применялись только тема и звук. Акцент, фон окна и качество
    /// анимаций записывались в конфиг, но сюда никогда не попадали, поэтому
    /// после перезапуска молча сбрасывались. Теперь восстанавливаются все.</para>
    /// </summary>
    public static void ApplyConfigToApp(AppConfigData data)
    {
        if (data is null) return;

        // Тема применяется по ИМЕНИ, а не по индексу: перестановка элементов
        // в enum AppTheme больше не переназначает тему пользователя.
        if (Enum.TryParse<AppTheme>(data.Theme, ignoreCase: true, out var theme)
            && Enum.IsDefined(typeof(AppTheme), theme))
        {
            ThemeManager.Instance.Theme = theme;
        }

        // Акцент по имени.
        if (!string.IsNullOrWhiteSpace(data.Accent))
        {
            var acc = ThemeManager.Instance.Accents
                .FirstOrDefault(a => string.Equals(a.Name, data.Accent, StringComparison.OrdinalIgnoreCase));
            if (acc != null) ThemeManager.Instance.Accent = acc;
        }

        // Фон окна: пустая строка = следовать теме.
        ThemeManager.Instance.BackdropOverride =
            !string.IsNullOrWhiteSpace(data.Backdrop) &&
            Enum.TryParse<BackdropType>(data.Backdrop, ignoreCase: true, out var bd) && bd != BackdropType.None
                ? bd
                : null;

        ThemeManager.Instance.AnimationQuality =
            Enum.IsDefined(typeof(AnimationQuality), data.AnimationQuality)
                ? (AnimationQuality)data.AnimationQuality
                : AnimationQuality.Maximum;

        ThemeManager.Instance.LiveBackdropEnabled = data.LiveBackdropEnabled;

        HapticAudio.Enabled = data.HapticAudioEnabled;
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
        if (!Directory.Exists(dir))
        {
            StatusMessage.Text = "Папка конфигурации не найдена.";
            return;
        }

        try
        {
            // ArgumentList вместо строковой интерполяции: путь может содержать пробелы.
            var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            psi.ArgumentList.Add(dir);
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            StatusMessage.Text = $"Не удалось открыть папку: {ex.Message}";
        }
    }

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
            "Донат",
            MessageBoxButton.OK,
            success ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        Close();
    }
}
