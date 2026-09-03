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
    Acrylic
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
        new AccentOption("Синий", (Color)ColorConverter.ConvertFromString("#0078D4")),
        new AccentOption("Фиолетовый", (Color)ColorConverter.ConvertFromString("#7446AC")),
        new AccentOption("Зелёный", (Color)ColorConverter.ConvertFromString("#107C10")),
        new AccentOption("Оранжевый", (Color)ColorConverter.ConvertFromString("#CA5010")),
        new AccentOption("Красный", (Color)ColorConverter.ConvertFromString("#D13438")),
        new AccentOption("Бирюзовый", (Color)ColorConverter.ConvertFromString("#038387")),
        new AccentOption("Розовый", (Color)ColorConverter.ConvertFromString("#C239B3")),
        new AccentOption("Жёлтый", (Color)ColorConverter.ConvertFromString("#F2C811")),
    };

    private AppTheme _theme = AppTheme.MicaLight;
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

    public bool IsDark => Theme is AppTheme.Dark or AppTheme.MicaDark or AppTheme.Acrylic;
    public BackdropType Backdrop => Theme switch
    {
        AppTheme.MicaLight => BackdropType.Mica,
        AppTheme.MicaDark => BackdropType.MicaAlt,
        AppTheme.Acrylic => BackdropType.Acrylic,
        _ => BackdropType.None
    };

    private ThemeManager()
    {
        _accent = Accents[0]; // системный по умолчанию
        if (SystemAccent.IsSystemDarkTheme())
        {
            _theme = AppTheme.MicaDark;
        }
    }

    public Color AccentColor => Accent.IsSystem ? SystemAccent.GetSystemAccent() : Accent.Color;

    public string ThemeDisplayName(AppTheme t) => t switch
    {
        AppTheme.Light => "☀ Светлая",
        AppTheme.Dark => "☾ Тёмная",
        AppTheme.MicaLight => "◈ Mica светлая",
        AppTheme.MicaDark => "◈ Mica тёмная",
        AppTheme.Acrylic => "⬣ Acrylic",
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

        // Фоны: для Mica/Acrylic — полупрозрачные, чтобы было видно системный блюр
        (Color window, Color card, Color border) = Theme switch
        {
            AppTheme.Light => (C("#F3F3F3"), C("#FFFFFF"), C("#E5E5E5")),
            AppTheme.Dark => (C("#202020"), C("#2D2D2D"), C("#3A3A3A")),
            AppTheme.MicaLight => (C("#F3F3F3", 0xE8), C("#FFFFFF", 0xB0), C("#E0E0E0")),
            AppTheme.MicaDark => (C("#202020", 0xE8), C("#2C2C2C", 0xA8), C("#3A3A3A")),
            AppTheme.Acrylic => dark ? (C("#1E1E1E", 0xC8), C("#2B2B2B", 0x90), C("#404040"))
                                     : (C("#F0F0F0", 0xC8), C("#FFFFFF", 0x90), C("#D8D8D8")),
            _ => (C("#F3F3F3"), C("#FFFFFF"), C("#E5E5E5"))
        };

        Color primary = dark ? C("#FFFFFF") : C("#1B1B1B");
        Color secondary = dark ? C("#C7C7C7") : C("#605E5C");
        Color track = dark ? C("#3A3A3A") : C("#E6E6E6");
        Color titleFg = dark ? C("#FFFFFF") : C("#1B1B1B");
        Color hover = dark ? C("#3A3A3A") : C("#EAEAEA");
        Color listHover = dark ? C("#333333") : C("#F5F5F5");
        Color graphGrid = dark ? C("#333333") : C("#E3E3E3");
        Color graphFill = Color.FromArgb(0x55, accent.R, accent.G, accent.B);

        Color glowAccent = Color.FromArgb(0x40, accent.R, accent.G, accent.B);
        Color glassBorder = dark ? Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x35, 0x00, 0x00, 0x00);
        Color subtleBorder = dark ? Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1F, 0x00, 0x00, 0x00);
        Color chipBg = dark ? Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x22, 0x00, 0x00, 0x00);

        Color headerBg = dark ? C("#181B24") : C("#F1F5F9");
        Color headerFg = dark ? C("#94A3B8") : C("#475569");
        Color headerBorder = dark ? C("#262D3D") : C("#E2E8F0");
        Color headerHover = dark ? C("#222836") : C("#E2E8F0");
        Color listRowSelected = dark ? Color.FromArgb(0x35, accent.R, accent.G, accent.B) : Color.FromArgb(0x25, accent.R, accent.G, accent.B);
        Color navDockBg = dark ? Color.FromArgb(0x50, 0x0F, 0x11, 0x18) : Color.FromArgb(0x20, 0x00, 0x00, 0x00);
        Color ribbonBg = dark ? Color.FromArgb(0x70, 0x16, 0x1A, 0x24) : Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF);
        Color inputBg = dark ? Color.FromArgb(0x50, 0x11, 0x14, 0x1D) : Color.FromArgb(0xF5, 0xFF, 0xFF, 0xFF);

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

        OnChanged(nameof(IsDark));
        OnChanged(nameof(Backdrop));
        OnChanged(nameof(AccentColor));

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
        // amount: -1..1
        double f(double v) => Math.Clamp(amount >= 0 ? v + (255 - v) * amount : v * (1 + amount), 0, 255);
        return Color.FromRgb((byte)f(c.R), (byte)f(c.G), (byte)f(c.B));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
