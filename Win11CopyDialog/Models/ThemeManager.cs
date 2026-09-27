using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Win11CopyDialog.Helpers;

namespace Win11CopyDialog.Models;

/// <summary>Темы оформления. Порядок НЕ важен: настройки хранятся по имени, UI — по имени.</summary>
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
    MinimalWhite,
    /// <summary>Космос: глубокий индиго, туманности, живой звёздный фон.</summary>
    CosmicNebula,
    /// <summary>Терминал: амбер-фосфор на почти чёрном.</summary>
    TerminalAmber,
    /// <summary>Глубина океана: холодные бирюзовые тона.</summary>
    DeepSea,
    /// <summary>Розовый кварц: светлая пастельная тема.</summary>
    RoseQuartz
}

/// <summary>Качество анимаций. Управляет FPS, числом слоёв и объёмом эффектов.</summary>
public enum AnimationQuality
{
    /// <summary>Без декоративной анимации, 30 FPS, без части эффектов. Для слабых машин и VM.</summary>
    Economy = 0,
    /// <summary>60 FPS, живой фон, полные эффекты. Значение по умолчанию.</summary>
    Maximum = 1
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
    private SolidColorBrush? _brush;
    public SolidColorBrush Brush => _brush ??= Frozen(new SolidColorBrush(Color));
    public override string ToString() => Name;

    internal static SolidColorBrush Frozen(SolidColorBrush b)
    {
        if (CanFreeze) b.Freeze();
        return b;
    }

    internal static bool CanFreeze =>
        System.Windows.Application.Current?.Dispatcher?.CheckAccess() ?? true;
}

/// <summary>
/// Централизованное управление темами, акцентами и анимациями. Singleton для всего приложения.
///
/// Ключевой принцип: Apply() вычисляет ВСЕ семантические токены из палитры темы.
/// Раньше ключи перечислялись вручную, из-за чего ~30 токенов (LiquidGlassSurface*, GlassBorder*,
/// Media*) навсегда оставались в тёмно-синем цвете и светлые темы рисовали тёмные карточки.
/// Теперь забыть ключ невозможно: он выводится из палитры.
/// </summary>
public sealed class ThemeManager : INotifyPropertyChanged
{
    public static ThemeManager Instance { get; } = new();

    // ================= Палитра =================

    /// <summary>Семантические роли темы. Apply() выводит из них все токены ресурсов.</summary>
    private sealed record Palette
    {
        /// <summary>
        /// Преобразует закрытую палитру в публичное представление.
        /// </summary>
        /// <remarks>
        /// Единственный мост наружу: наружу отдаётся структура только для
        /// чтения, а сама палитра остаётся под контролем ThemeManager.
        /// </remarks>
        public ThemeColors ToColors() => new(
            Window, Card, Border, Primary, Secondary,
            PreferredAccent ?? Primary,
            Success, Warning, Danger, Info,
            IsDark, WindowRadius, HasLiveBackdrop);

        public required string Name { get; init; }
        public required bool IsDark { get; init; }
        public required Color Window { get; init; }
        public required Color Card { get; init; }
        public required Color Border { get; init; }
        public required Color Success { get; init; }
        public required Color Warning { get; init; }
        public required Color Danger { get; init; }
        public required Color Info { get; init; }
        /// <summary>Скругление окна в DIP. 0 = без скругления (OLED/минимализм).</summary>
        public double WindowRadius { get; init; } = 12;
        /// <summary>Фоновая анимация за окном (звёзды, туманности). Требует затрат CPU.</summary>
        public bool HasLiveBackdrop { get; init; }
        /// <summary>Свой акцент, если тема его диктует. null = уважать выбор пользователя.</summary>
        public Color? PreferredAccent { get; init; }
        public BackdropType DefaultBackdrop { get; init; } = BackdropType.None;

        public Color Primary => IsDark ? C("#FFFFFF") : C("#1B1B1B");
        public Color Secondary => IsDark ? C("#94A3B8") : C("#605E5C");
        public Color Tertiary => IsDark ? C("#64748B") : C("#8A8A8E");
        public Color Disabled => IsDark ? C("#475569") : C("#C4C4C7");
        public Color Track => IsDark ? C("#1A2130") : C("#E6E6E6");
        public Color Hover => IsDark ? C("#1B2230") : C("#EAEAEA");
        public Color ListHover => IsDark ? C("#182031") : C("#F5F5F5");
        public Color GraphGrid => IsDark ? C("#1C2434") : C("#E3E3E3");
        public Color HeaderBg => IsDark ? C("#0D121C") : C("#F1F5F9");
        public Color HeaderFg => Secondary;
        public Color HeaderBorder => IsDark ? C("#1B2233") : C("#E2E8F0");
        public Color HeaderHover => IsDark ? C("#161D2C") : C("#E2E8F0");
    }

    private static readonly Dictionary<AppTheme, Palette> Palettes = new()
    {
        [AppTheme.Light] = new Palette
        {
            Name = "☀ Windows Светлая", IsDark = false,
            Window = C("#F3F3F3"), Card = C("#FFFFFF"), Border = C("#E5E5E5"),
            Success = C("#0F9D68"), Warning = C("#B8860B"), Danger = C("#C42B1C"), Info = C("#0F6CBD"),
            WindowRadius = 8
        },
        [AppTheme.Dark] = new Palette
        {
            Name = "☾ Windows Тёмная", IsDark = true,
            Window = C("#202020"), Card = C("#2D2D2D"), Border = C("#3A3A3A"),
            Success = C("#00D68F"), Warning = C("#FFB020"), Danger = C("#FF4567"), Info = C("#4CC2FF"),
            WindowRadius = 8
        },
        [AppTheme.MicaLight] = new Palette
        {
            Name = "◈ Windows 11 Mica (Светлая)", IsDark = false,
            Window = C("#F3F3F3", 0xE8), Card = C("#FFFFFF", 0xB0), Border = C("#E0E0E0"),
            Success = C("#0F9D68"), Warning = C("#B8860B"), Danger = C("#C42B1C"), Info = C("#0F6CBD"),
            WindowRadius = 8, DefaultBackdrop = BackdropType.Mica
        },
        [AppTheme.MicaDark] = new Palette
        {
            Name = "◈ Windows 11 Mica (Тёмная)", IsDark = true,
            Window = C("#202020", 0xE8), Card = C("#2C2C2C", 0xA8), Border = C("#3A3A3A"),
            Success = C("#00D68F"), Warning = C("#FFB020"), Danger = C("#FF4567"), Info = C("#4CC2FF"),
            WindowRadius = 8, DefaultBackdrop = BackdropType.MicaAlt
        },
        [AppTheme.Acrylic] = new Palette
        {
            Name = "⬣ Fluent Acrylic", IsDark = true,
            Window = C("#1E1E1E", 0xC8), Card = C("#2B2B2B", 0x90), Border = C("#404040"),
            Success = C("#00D68F"), Warning = C("#FFB020"), Danger = C("#FF4567"), Info = C("#4CC2FF"),
            WindowRadius = 12, DefaultBackdrop = BackdropType.Acrylic
        },
        [AppTheme.CyberpunkDark] = new Palette
        {
            Name = "⚡ Cyberpunk 2077", IsDark = true,
            Window = C("#0B0E14"), Card = C("#111827"), Border = C("#1F2937"),
            Success = C("#00FF9C"), Warning = C("#FFD166"), Danger = C("#FF2E6B"), Info = C("#00E5FF"),
            WindowRadius = 4, PreferredAccent = C("#00E5FF")
        },
        [AppTheme.OledMidnight] = new Palette
        {
            Name = "🌑 OLED Midnight (True Black)", IsDark = true,
            Window = C("#000000"), Card = C("#0A0A0A"), Border = C("#1F1F1F"),
            Success = C("#00D68F"), Warning = C("#FFB020"), Danger = C("#FF4567"), Info = C("#4CC2FF"),
            WindowRadius = 0
        },
        [AppTheme.MatrixEmerald] = new Palette
        {
            Name = "💻 Matrix Emerald (Terminal)", IsDark = true,
            Window = C("#040D07"), Card = C("#0A1A0F"), Border = C("#12381E"),
            Success = C("#00FF6A"), Warning = C("#B7FF3C"), Danger = C("#FF3B3B"), Info = C("#00D68F"),
            WindowRadius = 4, PreferredAccent = C("#00FF6A")
        },
        [AppTheme.SunsetAmber] = new Palette
        {
            Name = "🔥 Sunset Amber (Warm Gold)", IsDark = true,
            Window = C("#14100E"), Card = C("#1F1815"), Border = C("#382A22"),
            Success = C("#9BE15D"), Warning = C("#FFB020"), Danger = C("#FF5E3A"), Info = C("#FFD166"),
            WindowRadius = 14, PreferredAccent = C("#FFB020")
        },
        [AppTheme.RoyalIndigo] = new Palette
        {
            Name = "🔮 Royal Indigo (Deep Violet)", IsDark = true,
            Window = C("#0B0E1F"), Card = C("#131936"), Border = C("#222B57"),
            Success = C("#00D68F"), Warning = C("#FFC857"), Danger = C("#FF5C8A"), Info = C("#9D7BFF"),
            WindowRadius = 16, PreferredAccent = C("#7C5CFF")
        },
        [AppTheme.MotionGlass] = new Palette
        {
            Name = "✨ Motion Commander Glass OS", IsDark = true,
            Window = C("#050912"), Card = C("#0A1426", 0xB8), Border = C("#78BEFF", 0x14),
            Success = C("#00D68F"), Warning = C("#FFB020"), Danger = C("#FF4567"), Info = C("#00D4FF"),
            WindowRadius = 16, HasLiveBackdrop = true, PreferredAccent = C("#168CFF")
        },
        [AppTheme.MinimalWhite] = new Palette
        {
            Name = "⬜ Minimal White (чистая светлая)", IsDark = false,
            Window = C("#FFFFFF"), Card = C("#F9F9F9"), Border = C("#EAEAEA"),
            Success = C("#0F9D68"), Warning = C("#B8860B"), Danger = C("#C42B1C"), Info = C("#0F6CBD"),
            WindowRadius = 0
        },
        [AppTheme.CosmicNebula] = new Palette
        {
            Name = "🌌 Cosmic Nebula (космос)", IsDark = true,
            Window = C("#04050F"), Card = C("#0B0E22", 0xC4), Border = C("#8B7BFF", 0x26),
            Success = C("#3DF5B0"), Warning = C("#FFB84D"), Danger = C("#FF5C8A"), Info = C("#00E5FF"),
            WindowRadius = 18, HasLiveBackdrop = true, PreferredAccent = C("#7B5CFF")
        },
        [AppTheme.TerminalAmber] = new Palette
        {
            Name = "🖥 Terminal Amber (фосфор)", IsDark = true,
            Window = C("#0A0700"), Card = C("#140F02"), Border = C("#3D2E0A"),
            Success = C("#FFB000"), Warning = C("#FFCC44"), Danger = C("#FF5544"), Info = C("#FFD280"),
            WindowRadius = 2, PreferredAccent = C("#FFB000")
        },
        [AppTheme.DeepSea] = new Palette
        {
            Name = "🌊 Deep Sea (глубина)", IsDark = true,
            Window = C("#03121B"), Card = C("#06202E", 0xC0), Border = C("#0F4A63", 0x60),
            Success = C("#2FE0B0"), Warning = C("#FFC24D"), Danger = C("#FF6B6B"), Info = C("#35D0E8"),
            WindowRadius = 14, HasLiveBackdrop = true, PreferredAccent = C("#00C2D6")
        },
        [AppTheme.RoseQuartz] = new Palette
        {
            Name = "🌸 Rose Quartz (пастель)", IsDark = false,
            Window = C("#FDF4F6"), Card = C("#FFFFFF"), Border = C("#F0DCE2"),
            Success = C("#0F9D68"), Warning = C("#B8860B"), Danger = C("#D6336C"), Info = C("#845EC2"),
            WindowRadius = 16, PreferredAccent = C("#E6679B")
        }
    };

    // ================= Акценты =================

    public List<AccentOption> Accents { get; } = new()
    {
        new AccentOption("Системный", Colors.Transparent, isSystem: true),
        new AccentOption("Motion Glass Blue", C("#168CFF")),
        new AccentOption("Неон Циан", C("#00D4FF")),
        new AccentOption("Космо Фиолет", C("#7B5CFF")),
        new AccentOption("Фиолетовый", C("#7C5CFF")),
        new AccentOption("Янтарный", C("#FFB020")),
        new AccentOption("Успех Зеленый", C("#00D68F")),
        new AccentOption("Опасность Красный", C("#FF4567")),
        new AccentOption("Глубокая Бирюза", C("#00C2D6")),
        new AccentOption("Розовый Сахар", C("#E6679B")),
        new AccentOption("Мятный Неон", C("#3DF5B0"))
    };

    public static AppTheme[] AllThemes { get; } = (AppTheme[])Enum.GetValues(typeof(AppTheme));

    // ================= Состояние =================

    private AppTheme _theme = AppTheme.MotionGlass;
    public AppTheme Theme
    {
        get => _theme;
        set
        {
            if (_theme == value) return;
            _theme = value;
            // Тема со «своим» акцентом переключает акцент, только если стоял системный/дефолтный.
            var pref = Palettes[value].PreferredAccent;
            if (pref is Color pc && _accent.IsSystem) _accent = FindAccent(pc) ?? _accent;
            Refresh();
        }
    }

    private AccentOption _accent = null!;
    public AccentOption Accent
    {
        get => _accent;
        set
        {
            // null не принимаем: AccentOption безымянный акцент сделал бы интерфейс бесцветным.
            if (value is null) return;
            if (ReferenceEquals(_accent, value) || _accent.Name == value.Name) return;
            _accent = value;
            Refresh();
        }
    }

    private AnimationQuality _animationQuality = AnimationQuality.Maximum;
    public AnimationQuality AnimationQuality
    {
        get => _animationQuality;
        set { if (_animationQuality != value) { _animationQuality = value; Refresh(); } }
    }

    private bool _liveBackdropEnabled = true;
    /// <summary>Живой анимированный фон за окном. Требует темы с HasLiveBackdrop и качества Maximum.</summary>
    public bool LiveBackdropEnabled
    {
        get => _liveBackdropEnabled && Current.HasLiveBackdrop && _animationQuality == AnimationQuality.Maximum;
        set { if (_liveBackdropEnabled != value) { _liveBackdropEnabled = value; Refresh(); } }
    }

    /// <summary>Разрешена ли анимация в принципе (учитывает настройки Windows).</summary>
    public static bool AnimationsAllowed
    {
        get
        {
            try { return SystemParameters.ClientAreaAnimation; }
            catch { return true; }
        }
    }

    public bool IsDark => Current.IsDark;

    public bool IsAiEnabled => Theme != AppTheme.MinimalWhite;
    public Visibility AiVisibility => IsAiEnabled ? Visibility.Visible : Visibility.Collapsed;

    private BackdropType? _backdropOverride;
    /// <summary>Ручной выбор фона окна. null = следовать теме.</summary>
    public BackdropType? BackdropOverride
    {
        get => _backdropOverride;
        set { if (_backdropOverride != value) { _backdropOverride = value; Refresh(); } }
    }

    public BackdropType Backdrop => _backdropOverride ?? Current.DefaultBackdrop;

    private Palette Current => Palettes[_theme];

    /// <summary>
    /// Палитра темы для внешнего чтения: аудит контраста, инструменты
    /// экспорта, сторонние модули.
    /// </summary>
    /// <remarks>
    /// Раньше палитра была полностью закрытой, и проверить цвета извне
    /// было невозможно: единственный способ узнать контраст — запустить
    /// приложение и посмотреть глазами на экран. Теперь цвета доступны
    /// для вычислений, но сама палитра остаётся неизменяемой снаружи.
    /// </remarks>
    public ThemeColors GetColors(AppTheme theme) => Palettes[theme].ToColors();

    /// <summary>Палитра текущей темы.</summary>
    public ThemeColors CurrentColors => Current.ToColors();

    /// <summary>Публичное представление палитры: только чтение.</summary>
    public readonly record struct ThemeColors(
        Color Window,
        Color Card,
        Color Border,
        Color Text,
        Color TextMuted,
        Color Accent,
        Color Success,
        Color Warning,
        Color Danger,
        Color Info,
        bool IsDark,
        double WindowRadius,
        bool HasLiveBackdrop);

    private ThemeManager()
    {
        _accent = Accents.First(a => a.Name == "Motion Glass Blue");
        _theme = AppTheme.MotionGlass;
        Load();
    }

    private AccentOption? FindAccent(Color c)
    {
        AccentOption? best = null;
        int bestD = int.MaxValue;
        foreach (var a in Accents)
        {
            if (a.IsSystem) continue;
            int d = Math.Abs(a.Color.R - c.R) + Math.Abs(a.Color.G - c.G) + Math.Abs(a.Color.B - c.B);
            if (d < bestD) { bestD = d; best = a; }
        }
        return bestD < 24 ? best : null;
    }

    public Color AccentColor
    {
        get
        {
            if (Accent.IsSystem)
            {
                var sys = SystemAccent.GetSystemAccent();
                // Системный акцент — серый: подставляем акцент темы, иначе интерфейс станет бесцветным.
                int mx = Math.Max(sys.R, Math.Max(sys.G, sys.B));
                int mn = Math.Min(sys.R, Math.Min(sys.G, sys.B));
                if (mx - mn < 16) return Current.PreferredAccent ?? C("#168CFF");
                return sys;
            }
            return Accent.Color;
        }
    }

    public string ThemeDisplayName(AppTheme t) =>
        Palettes.TryGetValue(t, out var p) ? p.Name : t.ToString();

    // ================= Apply =================

    /// <summary>
    /// Пересобирает все токены ресурсов из палитры и уведомляет подписчиков.
    /// Порядок важен: сначала ресурсы, потом PropertyChanged. Раньше было наоборот,
    /// поэтому обработчики успевали перерисоваться старыми кистями.
    /// </summary>
    private void Refresh()
    {
        Apply();
        OnChanged(nameof(Theme));
        OnChanged(nameof(Accent));
        OnChanged(nameof(AccentColor));
        OnChanged(nameof(IsDark));
        OnChanged(nameof(Backdrop));
        OnChanged(nameof(IsAiEnabled));
        OnChanged(nameof(AiVisibility));
        OnChanged(nameof(AnimationQuality));
        OnChanged(nameof(LiveBackdropEnabled));
    }

    public void Apply()
    {
        var res = Application.Current?.Resources;
        if (res == null) return;

        var p = Current;
        bool dark = p.IsDark;
        Color accent = AccentColor;
        Color accentHover = Lighten(accent, dark ? 0.14f : -0.10f);
        Color accentPressed = Lighten(accent, dark ? -0.14f : -0.18f);
        Color accentText = ContrastText(accent);

        // Тема-нейтраль: одинаковая структура для всех 16 тем.
        var d = new Dictionary<string, object?>(140);

        // --- Фоны и поверхности ---
        d["WindowBackgroundBrush"] = Br(p.Window);
        d["CardBackgroundBrush"] = Br(p.Card);
        d["CardBorderBrush"] = Br(p.Border);
        d["GlassBorderBrush"] = Br(Color.FromArgb(dark ? (byte)0x1E : (byte)0x22, accent.R, accent.G, accent.B));
        d["SubtleBorderBrush"] = Br(Color.FromArgb(dark ? (byte)0x12 : (byte)0x14, accent.R, accent.G, accent.B));
        d["ChipBackgroundBrush"] = Br(dark ? C("#FFFFFF", 0x1A) : C("#000000", 0x14));

        // --- Уровни стекла ---
        // Раньше уровни 1/2/4/5/6 были жёстко тёмно-синими для всех тем — светлые темы
        // рисовали тёмные карточки на белом окне.
        byte a1 = dark ? (byte)0xF2 : (byte)0xFA;
        byte a2 = dark ? (byte)0xC4 : (byte)0xF0;
        byte a4 = dark ? (byte)0xF5 : (byte)0xFA;
        byte a5 = dark ? (byte)0xFF : (byte)0xFF;
        byte a6 = dark ? (byte)0xFF : (byte)0xFF;
        d["LiquidGlassMaterialLevel0Brush"] = Br(p.Window);
        d["LiquidGlassMaterialLevel1Brush"] = Br(Mix(p.Window, p.Card, dark ? 0.35f : 0.30f, a1));
        d["LiquidGlassMaterialLevel2Brush"] = Br(Mix(p.Window, p.Card, dark ? 0.55f : 0.55f, a2));
        d["LiquidGlassMaterialLevel3Brush"] = Br(p.Card);
        d["LiquidGlassMaterialLevel4Brush"] = Br(Mix(p.Card, Lighten(p.Card, dark ? 0.10f : -0.04f), 0.6f, a4));
        d["LiquidGlassMaterialLevel5Brush"] = Br(Mix(p.Card, Lighten(p.Card, dark ? 0.14f : -0.06f), 0.8f, a5));
        d["LiquidGlassMaterialLevel6Brush"] = Br(Mix(p.Card, Lighten(p.Card, dark ? 0.18f : -0.08f), 1.0f, a6));

        // --- Границы стекла ---
        Color lgB1 = Color.FromArgb(dark ? (byte)0x14 : (byte)0x12, accent.R, accent.G, accent.B);
        Color lgB2 = Color.FromArgb(dark ? (byte)0x22 : (byte)0x24, accent.R, accent.G, accent.B);
        Color lgB3 = p.Border;
        Color lgB4 = Color.FromArgb(dark ? (byte)0x38 : (byte)0x30, accent.R, accent.G, accent.B);
        Color lgB5 = Color.FromArgb(dark ? (byte)0x55 : (byte)0x44, accent.R, accent.G, accent.B);
        d["LiquidGlassBorderLevel1Brush"] = Br(lgB1);
        d["LiquidGlassBorderLevel2Brush"] = Br(lgB2);
        d["LiquidGlassBorderLevel3Brush"] = Br(lgB3);
        d["LiquidGlassBorderLevel4Brush"] = Br(lgB4);
        d["LiquidGlassBorderLevel5Brush"] = Br(lgB5);
        d["LiquidGlassSubtleBorderBrush"] = Br(lgB1);
        d["LiquidGlassHoverBorderBrush"] = Br(accent);
        d["LiquidGlassCardBorderBrush"] = Br(lgB3);
        d["LiquidGlassCardBackgroundBrush"] = Br(p.Card);
        d["LiquidGlassInputBackgroundBrush"] = Br(dark ? C("#000000", 0x30) : C("#FFFFFF", 0xF2));
        d["LiquidGlassInputBorderBrush"] = Br(lgB2);
        d["LiquidGlassInputHoverBackgroundBrush"] = Br(p.Hover);
        d["LiquidGlassInputFocusedBorderBrush"] = Br(accent);
        d["LiquidGlassHoverHighlight"] = Br(Color.FromArgb(dark ? (byte)0x14 : (byte)0x0A, accent.R, accent.G, accent.B));

        // --- Текст ---
        d["PrimaryTextBrush"] = Br(p.Primary);
        d["TextPrimaryBrush"] = Br(p.Primary);
        d["SecondaryTextBrush"] = Br(p.Secondary);
        d["TextSecondaryBrush"] = Br(p.Secondary);
        d["LiquidGlassTextSecondaryBrush"] = Br(p.Secondary);
        d["TextTertiaryBrush"] = Br(p.Tertiary);
        d["LiquidGlassTextTertiaryBrush"] = Br(p.Tertiary);
        d["MutedTextBrush"] = Br(p.Secondary);
        d["TextMutedBrush"] = Br(p.Tertiary);
        d["TextDisabledBrush"] = Br(p.Disabled);
        d["TitleForegroundBrush"] = Br(p.Primary);
        d["HeaderForegroundBrush"] = Br(p.HeaderFg);

        // --- Статусы (реальные токены, а не литералы в разметке) ---
        d["StatusSuccessBrush"] = Br(p.Success);
        d["StatusWarningBrush"] = Br(p.Warning);
        d["StatusDangerBrush"] = Br(p.Danger);
        d["SuccessGreenBrush"] = Br(p.Success);
        d["WarningAmberBrush"] = Br(p.Warning);
        d["ErrorRedBrush"] = Br(p.Danger);
        d["InfoBlueBrush"] = Br(p.Info);
        d["Color_Success"] = p.Success;
        d["Color_Warning"] = p.Warning;
        d["Color_Danger"] = p.Danger;
        d["Color_SuccessMuted"] = Tint(p.Success, 0x20);
        d["Color_WarningMuted"] = Tint(p.Warning, 0x20);
        d["Color_DangerMuted"] = Tint(p.Danger, 0x25);
        d["Color_DangerHover"] = Tint(p.Danger, 0x3A);

        // --- Акцент ---
        d["AccentBrush"] = Br(accent);
        d["AccentHoverBrush"] = Br(accentHover);
        d["AccentPressedBrush"] = Br(accentPressed);
        d["AccentForegroundBrush"] = Br(accentText);
        d["AccentMutedBrush"] = Br(Tint(accent, 0x35));

        // Акцент как ЦВЕТ ТЕКСТА. Отдельный ресурс нужен потому, что сам
        // акцент в светлых темах как текст нечитаем: розовый на белом
        // даёт 2.87:1, бирюзовый — 2.06:1. Светлеть нельзя, приближение
        // к фону только ухудшает. Здесь акцент затемняется до прохождения
        // порога WCAG AA, а AccentBrush остаётся для заливок и рамок,
        // где порог к тексту не применяется.
        d["AccentTextBrush"] = Br(Helpers.Contrast.EnsureReadable(accent, p.Card));

        // Семантические цвета как ТЕКСТ. Проблема та же: в светлых темах
        // зелёный «успех» и янтарный «предупреждение» на белой карточке
        // дают 3.3–3.5:1 при требуемых 4.5:1. Светлеть бесполезно —
        // цвет приблизится к фону. Затемнённые версии применяются там,
        // где цвет используется как надпись, а исходные остаются для
        // плашек, рамок и точек статуса.
        d["SuccessTextBrush"] = Br(Helpers.Contrast.EnsureReadable(p.Success, p.Card));
        d["WarningTextBrush"] = Br(Helpers.Contrast.EnsureReadable(p.Warning, p.Card));
        d["DangerTextBrush"]  = Br(Helpers.Contrast.EnsureReadable(p.Danger, p.Card));
        d["InfoTextBrush"]    = Br(Helpers.Contrast.EnsureReadable(p.Info, p.Card));
        Color glow = Tint(accent, dark ? (byte)0x40 : (byte)0x28);
        d["GlowAccentColor"] = glow;
        d["GlowAccentBrush"] = Br(glow);
        d["AccentGlowColor"] = glow;
        d["AccentGlowBrush"] = Br(glow);
        d["SelectionBrush"] = Br(Tint(accent, dark ? (byte)0x4D : (byte)0x33));
        d["ListRowSelectedBrush"] = Br(Tint(accent, dark ? (byte)0x38 : (byte)0x26));
        d["ControlActiveBackgroundBrush"] = Br(Tint(accent, dark ? (byte)0x38 : (byte)0x26));
        d["Color_AccentPrimary"] = accent;
        d["Color_AccentPrimaryDim"] = Lighten(accent, dark ? 0.1f : -0.1f);
        d["Color_AccentHover"] = accentHover;
        d["Color_AccentPressed"] = accentPressed;
        d["Color_AccentGlow"] = glow;
        d["Color_AccentSoft"] = Tint(accent, 0x25);
        d["Color_BorderSubtle"] = lgB1;
        d["Color_BorderDefault"] = lgB2;
        d["Color_BorderStrong"] = Lighten(accent, dark ? 0.1f : -0.1f);
        d["Color_BorderFocus"] = accent;
        d["CyberButtonGradientBrush"] = Grad(accent, Lighten(accent, dark ? 0.3f : -0.25f), new Point(0, 0), new Point(1, 1));

        // --- Хром шапки / навигации / ввода ---
        d["HeaderBackgroundBrush"] = Br(p.HeaderBg);
        d["HeaderBorderBrush"] = Br(p.HeaderBorder);
        d["HeaderHoverBrush"] = Br(p.HeaderHover);
        d["NavDockBackgroundBrush"] = Br(Tint(dark ? C("#05070C") : C("#FFFFFF"), dark ? (byte)0x50 : (byte)0x70));
        d["RibbonBackgroundBrush"] = Br(dark ? C("#0B0F18", 0xE0) : C("#FFFFFF", 0xE6));
        d["InputBackgroundBrush"] = Br(dark ? C("#000000", 0x30) : C("#FFFFFF", 0xF2));
        d["ControlBackgroundBrush"] = Br(dark ? C("#000000", 0x30) : C("#FFFFFF", 0xF2));
        d["ControlHoverBackgroundBrush"] = Br(p.Hover);
        d["HoverBrush"] = Br(p.Hover);
        d["ListHoverBrush"] = Br(p.ListHover);
        d["ProgressTrackBrush"] = Br(p.Track);
        d["GraphGridBrush"] = Br(p.GraphGrid);
        d["GraphFillBrush"] = Br(Tint(accent, 0x55));

        // --- Скроллбары ---
        d["ScrollTrackBrush"] = Br(dark ? C("#FFFFFF", 0x08) : C("#000000", 0x08));
        d["ScrollThumbBrush"] = Br(dark ? C("#94A3B8", 0x38) : C("#64748B", 0x38));
        d["ScrollThumbHoverBrush"] = Br(accent);
        d["ScrollThumbPressedBrush"] = Br(accentPressed);
        d["ScrollbarThumbBrush"] = Br(dark ? C("#94A3B8", 0x38) : C("#64748B", 0x38));
        d["ScrollbarThumbHoverBrush"] = Br(accent);

        // --- Контекстные меню ---
        d["ContextMenuBackground"] = Br(dark ? C("#1E2330", 0xF8) : C("#FAFAFC", 0xF8));
        d["ContextMenuBorder"] = Br(lgB2);
        d["ContextMenuForeground"] = Br(p.Primary);
        d["ContextMenuSecondaryForeground"] = Br(p.Secondary);
        d["ContextMenuHover"] = Br(Tint(accent, dark ? (byte)0x1C : (byte)0x14));
        d["ContextMenuPressed"] = Br(Tint(accent, dark ? (byte)0x35 : (byte)0x22));
        d["ContextMenuDisabled"] = Br(p.Disabled);
        d["ContextMenuDanger"] = Br(p.Danger);
        d["ContextMenuDangerHover"] = Br(Tint(p.Danger, 0x28));
        d["ContextMenuDivider"] = Br(dark ? C("#FFFFFF", 0x12) : C("#000000", 0x0E));
        d["ContextMenuShadowColor"] = dark ? C("#000000", 0x80) : C("#000000", 0x28);

        // --- Типографика: темы с моноширинным характером ---
        FontFamily? body = Theme is AppTheme.MatrixEmerald or AppTheme.TerminalAmber or AppTheme.CyberpunkDark
            ? new FontFamily("Cascadia Mono, Consolas, Courier New, monospace")
            : null;
        FontFamily? display = Theme == AppTheme.CyberpunkDark
            ? new FontFamily("Bahnschrift, Segoe UI, sans-serif")
            : null;
        FontFamily? defBody = body ?? new FontFamily("Segoe UI Variable Text, Segoe UI, -apple-system, BlinkMacSystemFont, Roboto, sans-serif");
        FontFamily? defDisplay = display ?? new FontFamily("Segoe UI Variable Display, Segoe UI, -apple-system, BlinkMacSystemFont, Roboto, sans-serif");
        TryFreeze(defBody); TryFreeze(defDisplay);
        d["BaseFontFamily"] = defBody;
        d["BaseFontFamilyDisplay"] = defDisplay;
        d["MonospaceFontFamily"] = new FontFamily("Cascadia Code, Consolas, Courier New, monospace");
        d["FontFamilyText"] = defBody;
        d["FontFamilySystem"] = defDisplay;
        d["FontFamilyCode"] = new FontFamily("Cascadia Code, Consolas, Courier New, monospace");

        // --- Геометрия окна ---
        d["RadiusWindow"] = new CornerRadius(p.WindowRadius);
        d["RadiusS"] = new CornerRadius(Math.Min(8, p.WindowRadius));
        d["RadiusSmall"] = new CornerRadius(Math.Min(8, p.WindowRadius));
        d["RadiusM"] = new CornerRadius(Math.Min(12, p.WindowRadius));
        d["RadiusNormal"] = new CornerRadius(Math.Min(12, p.WindowRadius));
        d["RadiusMedium"] = new CornerRadius(Math.Min(12, p.WindowRadius));
        d["RadiusL"] = new CornerRadius(Math.Min(16, p.WindowRadius));
        d["RadiusLarge"] = new CornerRadius(Math.Min(16, p.WindowRadius));
        d["RadiusXL"] = new CornerRadius(Math.Min(18, p.WindowRadius));
        d["RadiusFloating"] = new CornerRadius(Math.Min(18, p.WindowRadius));
        d["RadiusModal"] = new CornerRadius(Math.Min(20, p.WindowRadius));

        // --- Цветовые токены (для мест, читающих Color напрямую) ---
        d["Color_BgDeep"] = p.Window;
        d["Color_BgDeepLightened"] = Mix(p.Window, p.Card, 0.5f, 0xFF);
        d["Color_BgVignette"] = Lighten(p.Window, dark ? -0.4f : -0.05f);
        d["Color_SurfaceL1"] = Mix(p.Window, p.Card, 0.25f, 0xFF);
        d["Color_SurfaceL2"] = Mix(p.Window, p.Card, 0.50f, 0xFF);
        d["Color_SurfaceL3"] = Mix(p.Window, p.Card, 0.75f, 0xFF);
        d["Color_SurfaceL4"] = p.Card;
        d["Color_SurfaceModal"] = Mix(p.Card, p.Primary, dark ? 0.05f : 0.02f, 0xFF);
        d["Color_TextPrimary"] = p.Primary;
        d["Color_TextSecondary"] = p.Secondary;
        d["Color_TextTertiary"] = p.Tertiary;
        d["Color_TextMuted"] = p.Tertiary;
        d["Color_TextDisabled"] = p.Disabled;
        d["Color_HighlightEdge"] = dark ? C("#FFFFFF", 0x33) : C("#000000", 0x14);
        d["Color_ShadowAmbient"] = dark ? C("#000000", 0x80) : C("#000000", 0x1F);
        d["Color_ShadowElevated"] = dark ? C("#000000", 0xA0) : C("#000000", 0x2E);
        d["Color_ChromaticCyan"] = C("#00E5FF");
        d["Color_ChromaticViolet"] = C("#B366FF");
        d["Color_ChromaticMagenta"] = C("#F43F5E");
        d["Color_LensHighlightPristine"] = dark ? C("#FFFFFF", 0x59) : C("#FFFFFF", 0xCC);
        d["Color_LensHighlightDiffuse"] = dark ? C("#FFFFFF", 0x26) : C("#FFFFFF", 0x77);
        d["Color_LensRefractDark"] = dark ? C("#11111A", 0x0D) : C("#000000", 0x14);

        // --- Оптика (только для «стеклянных» тёмных тем; в светлых она даёт грязь) ---
        bool optics = dark;
        d["LiquidGlassLensChromaticBorder"] = optics
            ? Grad([
                Tint(accent, 0x22), Tint(C("#FFFFFF"), 0x35), Tint(C("#FFFFFF"), 0x10),
                Tint(accent, 0x08), Tint(Lighten(accent, 0.25f), 0x1E), Tint(accent, 0x15)
              ], 0, 0, 1, 1)
            : Grad([C("#FFFFFF", 0xFF), C("#FFFFFF", 0xE0), C("#FFFFFF", 0xFF)], 0, 0, 1, 1);
        d["LiquidGlassMultiBevelBrush"] = optics
            ? Grad([Tint(C("#FFFFFF"), 0x3A), Tint(C("#FFFFFF"), 0x14), Tint(C("#FFFFFF"), 0x04), Tint(accent, 0x18)], 0, 0, 0, 1)
            : Grad([C("#FFFFFF", 0xFF), C("#FFFFFF", 0xF2), C("#FFFFFF", 0xFF)], 0, 0, 0, 1);

        // --- Запись в ресурсы приложения ---
        foreach (var kv in d)
        {
            if (kv.Value is null) continue;
            if (res.Contains(kv.Key)) res[kv.Key] = kv.Value;
            else res.Add(kv.Key, kv.Value);
        }

        // --- Фон ко всем открытым окнам ---
        if (Application.Current != null)
        {
            var corners = p.WindowRadius <= 0.5 ? WindowCornerPreference.DoNotRound : WindowCornerPreference.Round;
            foreach (Window w in Application.Current.Windows)
            {
                if (w.IsLoaded) BackdropHelper.Apply(w, Backdrop, dark, corners);
            }
        }
    }

    // ================= Утилиты =================

    private static Color C(string hex, byte alpha = 0xFF)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        c.A = alpha;
        return c;
    }

    private static SolidColorBrush Br(Color c) => AccentOption.Frozen(new SolidColorBrush(c));

    private static LinearGradientBrush Grad(Color[] stops, double x1, double y1, double x2, double y2)
    {
        var b = new LinearGradientBrush(
            new GradientStopCollection(stops.Select(s => new GradientStop(s, 0)).ToList()),
            new Point(x1, y1), new Point(x2, y2));
        if (AccentOption.CanFreeze) b.Freeze();
        return b;
    }

    private static LinearGradientBrush Grad(Color a, Color b, Point start, Point end)
    {
        var brush = new LinearGradientBrush(
            new GradientStopCollection { new GradientStop(a, 0.0), new GradientStop(b, 1.0) },
            start, end);
        if (AccentOption.CanFreeze) brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Замораживает объект, если он Freezable. FontFamily, CornerRadius и Double
    /// не являются Freezable, поэтому принимаем object.
    /// </summary>
    private static void TryFreeze(object? o)
    {
        if (o is Freezable f && AccentOption.CanFreeze && f.CanFreeze) f.Freeze();
    }

    private static Color Tint(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    /// <summary>Линейное смешение с сохранением альфа-канала результата.</summary>
    private static Color Mix(Color a, Color b, float t, byte alpha)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(alpha,
            (byte)Math.Clamp(a.R + (b.R - a.R) * t, 0, 255),
            (byte)Math.Clamp(a.G + (b.G - a.G) * t, 0, 255),
            (byte)Math.Clamp(a.B + (b.B - a.B) * t, 0, 255));
    }

    private static Color Lighten(Color c, float amount)
    {
        float f(float v) => amount >= 0 ? v + (255 - v) * amount : v * (1 + amount);
        return Color.FromRgb((byte)Math.Clamp(f(c.R), 0, 255), (byte)Math.Clamp(f(c.G), 0, 255), (byte)Math.Clamp(f(c.B), 0, 255));
    }

    /// <summary>
    /// Читаемый цвет текста поверх акцента.
    /// </summary>
    /// <remarks>
    /// <para>Раньше здесь стояло угадывание по порогу яркости 0.42:
    /// «если светлее — чёрный, иначе белый». Порог был взят на глаз и не
    /// соответствовал WCAG: при акценте на границе читаемость оказывалась
    /// 2.5:1 при требуемых 4.5:1.</para>
    ///
    /// <para>Теперь сравниваются оба кандидата численно, и выбирается тот,
    /// у которого отношение контраста выше. Если ни один не достигает
    /// порога, выбирается лучший из доступных — частично читаемый текст
    /// лучше, чем вредное решение по порогу.</para>
    /// </remarks>
    private static Color ContrastText(Color bg)
    {
        var dark = C("#101014");
        var light = C("#FFFFFF");

        double darkRatio = Helpers.Contrast.Ratio(dark, bg);
        double lightRatio = Helpers.Contrast.Ratio(light, bg);

        return lightRatio >= darkRatio ? light : dark;
    }

    /// <summary>
    /// Цвет акцента, пригодный для ТЕКСТА на подложке.
    /// </summary>
    /// <remarks>
    /// <para>Акцент сам по себе часто не проходит WCAG AA как текст:
    /// розовый <c>#E6679B</c> на белом даёт 2.87:1, а бирюзовый
    /// <c>#00C2D6</c> на белом — 2.06:1. Светлеть такой цвет нельзя,
    /// он только приблизится к фону.</para>
    ///
    /// <para>Поэтому для текста используется затемнённая версия акцента,
    /// а сам акцент остаётся для заливок, рамок и значков, где порог
    /// WCAG к тексту не применяется. Без этого разделения акцентный
    /// текст был нечитаем в светлых темах.</para>
    /// </remarks>
    public Color AccentAsText(ThemeColors colors)
        => Helpers.Contrast.EnsureReadable(colors.Accent, colors.Card);

    // ================= Сохранение =================

    private static string StatePath => AppPaths.ThemeFile;

    private sealed class State
    {
        public string? Theme { get; set; }
        public string? Accent { get; set; }
        public string? Backdrop { get; set; }
        public int Quality { get; set; } = 1;
        public bool LiveBackdrop { get; set; } = true;
    }

    /// <summary>
    /// Загружает тему/акцент/фон/качество анимаций. Раньше акцент и фон записывались,
    /// но никогда не читались — после перезапуска всё сбрасывалось.
    /// </summary>
    public void Load()
    {
        try
        {
            if (!File.Exists(StatePath)) return;
            var s = JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath));
            if (s == null) return;

            if (s.Accent is not null)
            {
                var a = Accents.FirstOrDefault(x => string.Equals(x.Name, s.Accent, StringComparison.OrdinalIgnoreCase));
                if (a != null) _accent = a;
            }
            if (s.Theme is not null && Enum.TryParse<AppTheme>(s.Theme, true, out var t) && Palettes.ContainsKey(t))
            {
                _theme = t;
                // Акцент темы восстанавливаем только если пользователь его не выбирал.
                if (s.Accent is null && Palettes[t].PreferredAccent is Color pc)
                    _accent = FindAccent(pc) ?? _accent;
            }
            if (s.Backdrop is not null && Enum.TryParse<BackdropType>(s.Backdrop, true, out var b))
                _backdropOverride = b;
            if (Enum.IsDefined(typeof(AnimationQuality), s.Quality))
                _animationQuality = (AnimationQuality)s.Quality;
            _liveBackdropEnabled = s.LiveBackdrop;
        }
        catch
        {
            // Повреждённый файл настроек не должен мешать запуску — дефолты уже выставлены.
        }
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(StatePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var s = new State
            {
                Theme = _theme.ToString(),
                Accent = _accent.IsSystem ? null : _accent.Name,
                Backdrop = _backdropOverride?.ToString(),
                Quality = (int)_animationQuality,
                LiveBackdrop = _liveBackdropEnabled
            };
            File.WriteAllText(StatePath, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Настройки — не критичные данные: отказ записи не должен ломать UI.
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
