using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using Win11CopyDialog.Helpers;

namespace Win11CopyDialog.Models;

public enum AppTheme
{
    Light,
    Dark,
    MicaLight,
    MicaDark,
    Acrylic,
    CyberpunkDark,
    OledMidnight,
    MatrixEmerald,
    SunsetAmber,
    RoyalIndigo,
    MotionGlass,
    MinimalWhite
}

public sealed class AccentOption
{
    public string Name { get; }
    public Color Color { get; }
    public bool IsSystem { get; }
    public AccentOption(string name, Color color, bool isSystem = false)
    {
        Name = name; Color = color; IsSystem = isSystem;
    }
    public SolidColorBrush Brush => new(Color);
}

/// <summary>Централизованное управление темами и акцентами. Singleton для всего приложения.</summary>
public sealed class ThemeManager : INotifyPropertyChanged
{
    public static ThemeManager Instance { get; } = new();

    public List<AccentOption> Accents { get; } = new()
    {
        new AccentOption("Системный", Colors.Transparent, isSystem: true),
        new AccentOption("Motion Glass Blue", (Color)ColorConverter.ConvertFromString("#168CFF")),
        new AccentOption("Неон Циан", (Color)ColorConverter.ConvertFromString("#00D4FF")),
        new AccentOption("Фиолетовый", (Color)ColorConverter.ConvertFromString("#7C5CFF")),
        new AccentOption("Янтарный", (Color)ColorConverter.ConvertFromString("#FFB020")),
        new AccentOption("Успех Зеленый", (Color)ColorConverter.ConvertFromString("#00D68F")),
        new AccentOption("Опасность Красный", (Color)ColorConverter.ConvertFromString("#FF4567")),
    };

    private AppTheme _theme = AppTheme.MotionGlass;
    public AppTheme Theme
    {
        get => _theme;
        set { if (_theme != value) { _theme = value; OnChanged(); Apply(); } }
    }

    private AccentOption _accent = null!;
    public AccentOption Accent
    {
        get => _accent;
        set { if (_accent != value) { _accent = value; OnChanged(); Apply(); } }
    }

    public bool IsDark => Theme is not (AppTheme.Light or AppTheme.MicaLight or AppTheme.MinimalWhite);
    
    public bool IsAiEnabled => Theme != AppTheme.MinimalWhite;
    public Visibility AiVisibility => IsAiEnabled ? Visibility.Visible : Visibility.Collapsed;
    
    public BackdropType Backdrop => Theme switch
    {
        AppTheme.MicaLight => BackdropType.Mica,
        AppTheme.MicaDark => BackdropType.MicaAlt,
        AppTheme.Acrylic => BackdropType.Acrylic,
        _ => BackdropType.None
    };

    private ThemeManager()
    {
        _accent = Accents.FirstOrDefault(a => a.Name == "Motion Glass Blue") ?? Accents[1]; // Синий (Fluent) fallback
        _theme = AppTheme.MotionGlass;
    }

    public Color AccentColor => Accent.IsSystem ? SystemAccent.GetSystemAccent() : Accent.Color;

    public string ThemeDisplayName(AppTheme t) => t switch
    {
        AppTheme.Light => "☀ Windows Светлая",
        AppTheme.Dark => "☾ Windows Тёмная",
        AppTheme.MicaLight => "◈ Windows 11 Mica (Светлая)",
        AppTheme.MicaDark => "◈ Windows 11 Mica (Тёмная)",
        AppTheme.Acrylic => "⬣ Fluent Acrylic",
        AppTheme.CyberpunkDark => "⚡ Cyberpunk 2077 Dark",
        AppTheme.OledMidnight => "🌑 OLED Midnight (True Black)",
        AppTheme.MatrixEmerald => "💻 Matrix Emerald (Terminal)",
        AppTheme.SunsetAmber => "🔥 Sunset Amber (Warm Gold)",
        AppTheme.RoyalIndigo => "🔮 Royal Indigo (Deep Violet)",
        AppTheme.MotionGlass => "✨ Motion Commander Glass OS",
        AppTheme.MinimalWhite => "⬜ Minimal White (Светлая чистая)",
        _ => t.ToString()
    };

    public void Apply()
    {
        var res = Application.Current?.Resources;
        if (res == null) return;

        bool dark = IsDark;
        Color accent = AccentColor;
        Color accentHover = Lighten(accent, dark ? 0.12 : -0.08);
        Color accentPressed = Lighten(accent, dark ? -0.12 : -0.16);

        // Цветовые палитры фонов и карточек
        (Color window, Color card, Color border) = Theme switch
        {
            AppTheme.Light => (C("#F3F3F3"), C("#FFFFFF"), C("#E5E5E5")),
            AppTheme.Dark => (C("#202020"), C("#2D2D2D"), C("#3A3A3A")),
            AppTheme.MicaLight => (C("#F3F3F3", 0xE8), C("#FFFFFF", 0xB0), C("#E0E0E0")),
            AppTheme.MicaDark => (C("#202020", 0xE8), C("#2C2C2C", 0xA8), C("#3A3A3A")),
            AppTheme.Acrylic => dark ? (C("#1E1E1E", 0xC8), C("#2B2B2B", 0x90), C("#404040"))
                                     : (C("#F0F0F0", 0xC8), C("#FFFFFF", 0x90), C("#D8D8D8")),
            AppTheme.CyberpunkDark => (C("#0B0E14"), C("#111827"), C("#1F2937")),
            AppTheme.OledMidnight => (C("#000000"), C("#0A0A0A"), C("#1F1F1F")),
            AppTheme.MatrixEmerald => (C("#040D07"), C("#0A1A0F"), C("#12381E")),
            AppTheme.SunsetAmber => (C("#14100E"), C("#1F1815"), C("#382A22")),
            AppTheme.RoyalIndigo => (C("#0B0E1F"), C("#131936"), C("#222B57")),
            AppTheme.MotionGlass => (C("#050912"), C("#0A1426", 0xB8), C("#78BEFF", 0x2E)), // Glass Background and Border
            AppTheme.MinimalWhite => (C("#FFFFFF"), C("#F9F9F9"), C("#EAEAEA")),
            _ => (C("#202020"), C("#2D2D2D"), C("#3A3A3A"))
        };

        Color primary = dark ? C("#FFFFFF") : C("#1B1B1B");
        Color secondary = dark ? C("#94A3B8") : C("#605E5C");
        Color track = dark ? C("#262D3D") : C("#E6E6E6");
        Color titleFg = dark ? C("#FFFFFF") : C("#1B1B1B");
        Color hover = dark ? C("#232936") : C("#EAEAEA");
        Color listHover = dark ? C("#1F2533") : C("#F5F5F5");
        Color graphGrid = dark ? C("#222938") : C("#E3E3E3");
        Color graphFill = Color.FromArgb(0x55, accent.R, accent.G, accent.B);

        Color glowAccent = Theme == AppTheme.MotionGlass ? Color.FromArgb(0x60, accent.R, accent.G, accent.B) : Color.FromArgb(0x40, accent.R, accent.G, accent.B);
        Color glassBorder = Theme == AppTheme.MotionGlass ? Color.FromArgb(0x2E, 0x78, 0xBE, 0xFF) : (dark ? Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x35, 0x00, 0x00, 0x00));
        Color subtleBorder = Theme == AppTheme.MotionGlass ? Color.FromArgb(0x15, 0x78, 0xBE, 0xFF) : (dark ? Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1F, 0x00, 0x00, 0x00));
        Color chipBg = Theme == AppTheme.MotionGlass ? Color.FromArgb(0x30, 0x08, 0x12, 0x22) : (dark ? Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x22, 0x00, 0x00, 0x00));

        Color headerBg = Theme == AppTheme.MotionGlass ? C("#08111F", 0xE0) : (dark ? C("#0F131D") : C("#F1F5F9"));
        Color headerFg = dark ? C("#94A3B8") : C("#475569");
        Color headerBorder = Theme == AppTheme.MotionGlass ? Color.FromArgb(0x2E, 0x78, 0xBE, 0xFF) : (dark ? C("#1E2536") : C("#E2E8F0"));
        Color headerHover = Theme == AppTheme.MotionGlass ? Color.FromArgb(0x30, 0x0F, 0x1E, 0x37) : (dark ? C("#1E2638") : C("#E2E8F0"));
        Color listRowSelected = Theme == AppTheme.MotionGlass ? Color.FromArgb(0x40, accent.R, accent.G, accent.B) : (dark ? Color.FromArgb(0x35, accent.R, accent.G, accent.B) : Color.FromArgb(0x25, accent.R, accent.G, accent.B));
        Color navDockBg = Theme == AppTheme.MotionGlass ? Color.FromArgb(0x80, 0x0A, 0x14, 0x26) : (dark ? Color.FromArgb(0x60, 0x08, 0x0B, 0x12) : Color.FromArgb(0x20, 0x00, 0x00, 0x00));
        Color ribbonBg = Theme == AppTheme.MotionGlass ? Color.FromArgb(0xB2, 0x08, 0x12, 0x22) : (dark ? Color.FromArgb(0x80, 0x11, 0x16, 0x22) : Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF));
        Color inputBg = Theme == AppTheme.MotionGlass ? Color.FromArgb(0x60, 0x08, 0x11, 0x1F) : (dark ? Color.FromArgb(0x60, 0x0D, 0x11, 0x1A) : Color.FromArgb(0xF5, 0xFF, 0xFF, 0xFF));

        Color scrollTrack = dark ? Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0C, 0x00, 0x00, 0x00);
        Color scrollThumb = dark ? Color.FromArgb(0x40, 0x94, 0xA3, 0xB8) : Color.FromArgb(0x40, 0x64, 0x74, 0x8B);
        Color scrollThumbHover = accent;
        Color scrollThumbPressed = Lighten(accent, dark ? 0.2 : -0.2);

        Color cyberGradEnd = Lighten(accent, dark ? 0.3 : -0.25);
        var cyberGrad = new LinearGradientBrush(accent, cyberGradEnd, new Point(0, 0), new Point(1, 1));

        Set("ScrollTrackBrush", new SolidColorBrush(scrollTrack));
        Set("ScrollThumbBrush", new SolidColorBrush(scrollThumb));
        Set("ScrollThumbHoverBrush", new SolidColorBrush(scrollThumbHover));
        Set("ScrollThumbPressedBrush", new SolidColorBrush(scrollThumbPressed));
        Set("WindowBackgroundBrush", new SolidColorBrush(window));
        Set("CardBackgroundBrush", new SolidColorBrush(card));
        Set("CardBorderBrush", new SolidColorBrush(border));
        Set("GlassBorderBrush", new SolidColorBrush(glassBorder));
        Set("SubtleBorderBrush", new SolidColorBrush(subtleBorder));
        Set("ChipBackgroundBrush", new SolidColorBrush(chipBg));
        Set("HeaderBackgroundBrush", new SolidColorBrush(headerBg));
        Set("HeaderForegroundBrush", new SolidColorBrush(headerFg));
        Set("HeaderBorderBrush", new SolidColorBrush(headerBorder));
        Set("HeaderHoverBrush", new SolidColorBrush(headerHover));
        Set("ListRowSelectedBrush", new SolidColorBrush(listRowSelected));
        Set("NavDockBackgroundBrush", new SolidColorBrush(navDockBg));
        Set("RibbonBackgroundBrush", new SolidColorBrush(ribbonBg));
        Set("InputBackgroundBrush", new SolidColorBrush(inputBg));
        Set("ControlBackgroundBrush", new SolidColorBrush(inputBg));
        Set("CyberButtonGradientBrush", cyberGrad);
        Set("PrimaryTextBrush", new SolidColorBrush(primary));
        Set("SecondaryTextBrush", new SolidColorBrush(secondary));
        Set("AccentBrush", new SolidColorBrush(accent));
        Set("AccentHoverBrush", new SolidColorBrush(accentHover));
        Set("AccentPressedBrush", new SolidColorBrush(accentPressed));
        Set("AccentForegroundBrush", new SolidColorBrush(Colors.White));
        Set("GlowAccentColor", glowAccent);
        Set("GlowAccentBrush", new SolidColorBrush(glowAccent));
        Set("ProgressTrackBrush", new SolidColorBrush(track));
        Set("TitleForegroundBrush", new SolidColorBrush(titleFg));
        Set("HoverBrush", new SolidColorBrush(hover));
        Set("ListHoverBrush", new SolidColorBrush(listHover));
        Set("GraphGridBrush", new SolidColorBrush(graphGrid));
        Set("GraphFillBrush", new SolidColorBrush(graphFill));

        // Context Menu Tokens
        Color contextBg = Theme == AppTheme.MotionGlass 
            ? Color.FromArgb(0xF2, 0x0B, 0x14, 0x24) 
            : (dark ? Color.FromArgb(0xF8, 0x1E, 0x23, 0x30) : Color.FromArgb(0xF8, 0xFA, 0xFA, 0xFC));
        Color contextBorder = Theme == AppTheme.MotionGlass
            ? Color.FromArgb(0x40, 0x78, 0xBE, 0xFF)
            : (dark ? Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x30, 0x00, 0x00, 0x00));
        Color contextHover = Theme == AppTheme.MotionGlass
            ? Color.FromArgb(0x28, 0x16, 0x8C, 0xFF)
            : (dark ? Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x10, 0x00, 0x00, 0x00));
        Color contextPressed = Theme == AppTheme.MotionGlass
            ? Color.FromArgb(0x45, 0x16, 0x8C, 0xFF)
            : (dark ? Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x20, 0x00, 0x00, 0x00));
        Color contextDisabled = dark ? Color.FromArgb(0x55, 0x94, 0xA3, 0xB8) : Color.FromArgb(0x55, 0x60, 0x5E, 0x5C);
        Color contextDanger = Color.FromRgb(0xFF, 0x45, 0x67);
        Color contextDangerHover = Color.FromArgb(0x2E, 0xFF, 0x45, 0x67);
        Color contextDivider = dark ? Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x15, 0x00, 0x00, 0x00);
        Color contextShadow = dark ? Color.FromArgb(0x90, 0x00, 0x00, 0x00) : Color.FromArgb(0x35, 0x00, 0x00, 0x00);

        Set("ContextMenuBackground", new SolidColorBrush(contextBg));
        Set("ContextMenuBorder", new SolidColorBrush(contextBorder));
        Set("ContextMenuForeground", new SolidColorBrush(primary));
        Set("ContextMenuSecondaryForeground", new SolidColorBrush(secondary));
        Set("ContextMenuHover", new SolidColorBrush(contextHover));
        Set("ContextMenuPressed", new SolidColorBrush(contextPressed));
        Set("ContextMenuDisabled", new SolidColorBrush(contextDisabled));
        Set("ContextMenuDanger", new SolidColorBrush(contextDanger));
        Set("ContextMenuDangerHover", new SolidColorBrush(contextDangerHover));
        Set("ContextMenuDivider", new SolidColorBrush(contextDivider));
        Set("ContextMenuShadowColor", contextShadow);

        // ================= Liquid Glass System Tokens =================
        Color lgLevel0 = window;
        Color lgLevel1 = dark ? C("#0A0F1A", (byte)(Theme == AppTheme.MotionGlass ? 0xCC : 0xF0)) : C("#F0F3F8", 0xFA);
        Color lgLevel2 = dark ? C("#0D1524", (byte)(Theme == AppTheme.MotionGlass ? 0xB8 : 0xF0)) : C("#FFFFFF", 0xF0);
        Color lgLevel3 = card;
        Color lgLevel4 = dark ? C("#16233B", (byte)(Theme == AppTheme.MotionGlass ? 0xD0 : 0xF5)) : C("#FFFFFF", 0xFA);
        Color lgLevel5 = dark ? C("#1C2C4A", (byte)(Theme == AppTheme.MotionGlass ? 0xEA : 0xFF)) : C("#FFFFFF", 0xFF);
        Color lgLevel6 = dark ? C("#223659", (byte)(Theme == AppTheme.MotionGlass ? 0xF5 : 0xFF)) : C("#FFFFFF", 0xFF);

        Color lgBorder1 = subtleBorder;
        Color lgBorder2 = glassBorder;
        Color lgBorder3 = border;
        Color lgBorder4 = dark ? Color.FromArgb(0x45, 0x78, 0xBE, 0xFF) : Color.FromArgb(0x35, 0x00, 0x00, 0x00);
        Color lgBorder5 = dark ? Color.FromArgb(0x60, 0x78, 0xBE, 0xFF) : Color.FromArgb(0x50, 0x00, 0x00, 0x00);

        Color textTertiary = dark ? C("#64748B") : C("#94A3B8");
        Color textDisabled = dark ? C("#475569") : C("#CBD5E1");

        Set("LiquidGlassMaterialLevel0Brush", new SolidColorBrush(lgLevel0));
        Set("LiquidGlassMaterialLevel1Brush", new SolidColorBrush(lgLevel1));
        Set("LiquidGlassMaterialLevel2Brush", new SolidColorBrush(lgLevel2));
        Set("LiquidGlassMaterialLevel3Brush", new SolidColorBrush(lgLevel3));
        Set("LiquidGlassMaterialLevel4Brush", new SolidColorBrush(lgLevel4));
        Set("LiquidGlassMaterialLevel5Brush", new SolidColorBrush(lgLevel5));
        Set("LiquidGlassMaterialLevel6Brush", new SolidColorBrush(lgLevel6));

        Set("LiquidGlassBorderLevel1Brush", new SolidColorBrush(lgBorder1));
        Set("LiquidGlassBorderLevel2Brush", new SolidColorBrush(lgBorder2));
        Set("LiquidGlassBorderLevel3Brush", new SolidColorBrush(lgBorder3));
        Set("LiquidGlassBorderLevel4Brush", new SolidColorBrush(lgBorder4));
        Set("LiquidGlassBorderLevel5Brush", new SolidColorBrush(lgBorder5));
        Set("LiquidGlassSubtleBorderBrush", new SolidColorBrush(lgBorder1));
        Set("LiquidGlassHoverBorderBrush", new SolidColorBrush(accent));

        Set("LiquidGlassCardBorderBrush", new SolidColorBrush(lgBorder3));
        Set("LiquidGlassCardBackgroundBrush", new SolidColorBrush(lgLevel3));
        Set("LiquidGlassInputBackgroundBrush", new SolidColorBrush(inputBg));
        Set("LiquidGlassInputBorderBrush", new SolidColorBrush(lgBorder2));
        Set("LiquidGlassInputHoverBackgroundBrush", new SolidColorBrush(hover));
        Set("LiquidGlassInputFocusedBorderBrush", new SolidColorBrush(accent));

        // 2211.zip Chromatic Lens Dispersion Border (Simulates RGB Channel Separation)
        if (Theme == AppTheme.MotionGlass)
        {
            var chromatic = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(0x60, 0x00, 0xE5, 0xFF), 0.0),  // Cyan Rim
                    new GradientStop(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF), 0.12), // Specular Peak
                    new GradientStop(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), 0.30),
                    new GradientStop(Color.FromArgb(0x15, 0x78, 0xBE, 0xFF), 0.65),
                    new GradientStop(Color.FromArgb(0x55, 0xB3, 0x66, 0xFF), 0.88), // Violet Rim
                    new GradientStop(Color.FromArgb(0x45, 0xF4, 0x3F, 0x5E), 1.0),  // Magenta Edge
                },
                new Point(0, 0),
                new Point(1, 1)
            );
            Set("LiquidGlassLensChromaticBorder", chromatic);

            var multiBevel = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(0x85, 0xFF, 0xFF, 0xFF), 0.0),
                    new GradientStop(Color.FromArgb(0x25, 0xFF, 0xFF, 0xFF), 0.18),
                    new GradientStop(Color.FromArgb(0x06, 0xFF, 0xFF, 0xFF), 0.60),
                    new GradientStop(Color.FromArgb(0x30, accent.R, accent.G, accent.B), 1.0),
                },
                new Point(0, 0),
                new Point(0, 1)
            );
            Set("LiquidGlassMultiBevelBrush", multiBevel);
        }

        Set("TextPrimaryBrush", new SolidColorBrush(primary));
        Set("TextSecondaryBrush", new SolidColorBrush(secondary));
        Set("TextTertiaryBrush", new SolidColorBrush(textTertiary));
        Set("TextDisabledBrush", new SolidColorBrush(textDisabled));

        Set("AccentMutedBrush", new SolidColorBrush(Color.FromArgb(0x35, accent.R, accent.G, accent.B)));
        Set("AccentGlowColor", glowAccent);
        Set("AccentGlowBrush", new SolidColorBrush(glowAccent));
        Set("ControlHoverBackgroundBrush", new SolidColorBrush(hover));
        Set("ControlActiveBackgroundBrush", new SolidColorBrush(listRowSelected));
        Set("SelectionBrush", new SolidColorBrush(Color.FromArgb(0x4D, accent.R, accent.G, accent.B)));
        Set("ScrollbarThumbBrush", new SolidColorBrush(scrollThumb));
        Set("ScrollbarThumbHoverBrush", new SolidColorBrush(scrollThumbHover));

        OnChanged(nameof(IsDark));
        OnChanged(nameof(Backdrop));
        OnChanged(nameof(AccentColor));
        OnChanged(nameof(IsAiEnabled));
        OnChanged(nameof(AiVisibility));

        // Применить фон ко всем открытым окнам
        if (Application.Current != null)
            foreach (Window w in Application.Current.Windows)
                BackdropHelper.Apply(w, Backdrop, dark);

        static void Set(string key, object value)
        {
            var r = Application.Current.Resources;
            if (r.Contains(key)) r[key] = value; else r.Add(key, value);
        }
        static Color C(string hex, byte alpha = 0xFF)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            c.A = alpha;
            return c;
        }
    }

    private static Color Lighten(Color c, double amount)
    {
        double f(double v) => Math.Clamp(amount >= 0 ? v + (255 - v) * amount : v * (1 + amount), 0, 255);
        return Color.FromRgb((byte)f(c.R), (byte)f(c.G), (byte)f(c.B));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
