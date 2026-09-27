using System.Windows;
using System.Windows.Media;
using Win11CopyDialog.Helpers;
using Win11CopyDialog.Models;

namespace Win11CopyDialog.Controls;

/// <summary>
/// Анимированный визуализатор процесса распаковки архива:
/// - Файлы и световые частицы вырываются наружу из квантового ядра архива;
/// - Расширяющиеся волны декомпрессии;
/// - Неоновое свечение и живой прогресс извлечения.
/// </summary>
public sealed class ExtractionVisualizer : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty =
        DependencyProperty.Register(nameof(Progress), typeof(double), typeof(ExtractionVisualizer),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    private sealed class ExpandParticle
    {
        public double Angle;
        public double Dist;
        public double Speed;
        public double Size;
    }

    private readonly List<ExpandParticle> _particles = new();
    private readonly Random _rnd = new();
    private DateTime _last = DateTime.Now;
    private double _time;
    private bool _running;
    /// <summary>
    /// Рендер нужен только когда элемент действительно нарисован.
    /// Раньше CompositionTarget.Rendering крутился всегда, в том числе в
    /// свёрнутом окне и на скрытой вкладке, то есть впустую.
    /// </summary>
    private bool ShouldRender =>
        IsVisible && IsLoaded && Visibility == Visibility.Visible &&
        Window.GetWindow(this) is { WindowState: not WindowState.Minimized, IsVisible: true };

    private bool _skipFrame;

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _last = DateTime.Now;   // не накапливать dt за время скрытия
        if (ShouldRender) InvalidateVisual();
    }

    // Замороженная кисть: незамороженную WPF клонирует при каждом обращении.
    private Brush _accent = Frozen(Color.FromRgb(16, 185, 129));
    private readonly System.ComponentModel.PropertyChangedEventHandler _themeHandler;

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        if (b.CanFreeze) b.Freeze();
        return b;
    }

    public ExtractionVisualizer()
    {
        MinHeight = 160;
        ClipToBounds = true;

        for (int i = 0; i < 60; i++)
        {
            _particles.Add(new ExpandParticle
            {
                Angle = _rnd.NextDouble() * Math.PI * 2,
                Dist = 16 + _rnd.NextDouble() * 100,
                Speed = 45 + _rnd.NextDouble() * 85,
                Size = 1.5 + _rnd.NextDouble() * 2.2
            });
        }

        Loaded += (_, _) =>
        {
            RefreshThemeBrushes();
            _last = DateTime.Now;
            _running = true;
            CompositionTarget.Rendering += OnRendering;
            IsVisibleChanged += OnVisibilityChanged;
        };
        Unloaded += (_, _) =>
        {
            _running = false;
            CompositionTarget.Rendering -= OnRendering;
            IsVisibleChanged -= OnVisibilityChanged;
        };

        // Раньше акцент читался только в Loaded: смена темы оставляла старый цвет.
        _themeHandler = (_, _) => RefreshThemeBrushes();
        Win11CopyDialog.Models.ThemeManager.Instance.PropertyChanged += _themeHandler;
    }

    private void RefreshThemeBrushes()
    {
        var r = Application.Current?.Resources;
        if (r == null) return;
        if (Lookup(r, "AccentBrush") is Brush b) _accent = b;
    }

    private static object? Lookup(ResourceDictionary dict, string key, int depth = 0)
    {
        if (depth > 4) return null;
        if (dict.Contains(key)) { try { return dict[key]; } catch { return null; } }
        foreach (var m in dict.MergedDictionaries)
        {
            if (m == null) continue;
            var v = Lookup(m, key, depth + 1);
            if (v != null) return v;
        }
        return null;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_running) return;
        if (!ShouldRender) return;

        if (ThemeManager.Instance.AnimationQuality == AnimationQuality.Economy)
        {
            _skipFrame = !_skipFrame;
            if (_skipFrame) return;
        }
        var now = DateTime.Now;
        double dt = Math.Min(0.05, (now - _last).TotalSeconds);
        _last = now;
        _time += dt;

        foreach (var p in _particles)
        {
            p.Dist += dt * p.Speed;
            if (p.Dist >= 120)
            {
                p.Dist = 16 + _rnd.NextDouble() * 10;
                p.Angle = _rnd.NextDouble() * Math.PI * 2;
            }
        }

        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth, h = ActualHeight;
        if (w < 40 || h < 40) return;

        var center = new Point(w / 2, h / 2);
        Color acc = (_accent as SolidColorBrush)?.Color ?? Color.FromRgb(16, 185, 129);

        // Раньше свойство Progress было объявлено как DependencyProperty, и
        // ExtractArchiveWindow его присваивал, но OnRender его НИКОГДА не читал:
        // визуализатор показывал декоративную анимацию без прогресса.
        double progress = Math.Clamp(Progress, 0, 100) / 100.0;

        // 1. Радиальное расширяющееся свечение (яркость растёт с прогрессом)
        byte glowAlpha = (byte)(35 + 70 * progress);
        var glow = new RadialGradientBrush(
            Color.FromArgb(glowAlpha, acc.R, acc.G, acc.B),
            Color.FromArgb(0, acc.R, acc.G, acc.B));
        if (glow.CanFreeze) glow.Freeze();
        dc.DrawEllipse(glow, null, center, 120, 80);

        // 2. Расширяющиеся концентрические волны
        double wavePhase = (_time * 1.8) % 1.0;
        var wavePen = FrameBrushCache.Pen4(acc, (1 - wavePhase) * 120 / 255.0, 1.5);
        dc.DrawEllipse(null, wavePen, center, 20 + wavePhase * 80, 15 + wavePhase * 55);

        // 3. Вылетающие наружу частицы файлов со шлейфами.
        //    Начало шлейфа отодвигается по мере роста прогресса — визуальный отклик.
        double tailOffset = 12 - 6 * progress;
        var pBrush = FrameBrushCache.Solid(acc, 200 / 255.0);
        var tailPen = FrameBrushCache.Pen4(acc, 90 / 255.0, 1.4);

        foreach (var p in _particles)
        {
            double x = center.X + Math.Cos(p.Angle) * p.Dist;
            double y = center.Y + Math.Sin(p.Angle) * (p.Dist * 0.65);

            double tx = center.X + Math.Cos(p.Angle) * Math.Max(0, p.Dist - tailOffset);
            double ty = center.Y + Math.Sin(p.Angle) * Math.Max(0, (p.Dist - tailOffset) * 0.65);
            dc.DrawLine(tailPen, new Point(tx, ty), new Point(x, y));

            dc.DrawEllipse(pBrush, null, new Point(x, y), p.Size, p.Size);
        }

        // 4. Дуга прогресса вокруг ядра: длина дуги = 360° * progress.
        //    Раньше индикатора прогресса не существовало вообще.
        if (progress > 0.001)
        {
            double rx = 26, ry = 20;
            double start = -90 - _time * 1.2;
            double sweep = 360 * progress;

            const int Steps = 64;
            var figure = new PathFigure { StartPoint = PointOnRing(center, rx, ry, start) };
            for (int i = 1; i <= Steps; i++)
            {
                double a = start + sweep * i / Steps;
                figure.Segments.Add(new LineSegment(PointOnRing(center, rx, ry, a), true));
            }

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            geometry.Freeze();

            var trackPen = FrameBrushCache.Pen4(acc, 50 / 255.0, 3.0);
            dc.DrawEllipse(null, trackPen, center, rx, ry);

            var arcBrush = FrameBrushCache.Solid(acc, 235 / 255.0);
            var arcPen = FrameBrushCache.PenFor(arcBrush, 3.0);
            dc.DrawGeometry(null, arcPen, geometry);
        }

        // 5. Центральное раскрывающеесь ядро архива
        var coreBrush = FrameBrushCache.Solid(acc, 220 / 255.0);
        dc.DrawEllipse(coreBrush, null, center, 18, 18);
        dc.DrawEllipse(Brushes.White, null, center, 6, 6);
    }

    /// <summary>Точка на эллипсе под заданным углом (в градусах).</summary>
    private static Point PointOnRing(Point center, double rx, double ry, double degrees)
    {
        double rad = degrees * Math.PI / 180.0;
        return new Point(center.X + Math.Cos(rad) * rx, center.Y + Math.Sin(rad) * ry);
    }
}
