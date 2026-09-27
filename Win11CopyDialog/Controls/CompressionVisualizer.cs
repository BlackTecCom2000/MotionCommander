using System.Windows;
using System.Windows.Media;
using Win11CopyDialog.Helpers;
using Win11CopyDialog.Models;

namespace Win11CopyDialog.Controls;

/// <summary>
/// Анимированный визуализатор процесса сжатия данных:
/// - Потоки блоков данных втягиваются снаружи в квантовое ядро-архив;
/// - Голографический кристалл вращается и уплотняет входящие частицы;
/// - Живое отображение коэффициента сжатия и сэкономленного места.
/// </summary>
public sealed class CompressionVisualizer : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty =
        DependencyProperty.Register(nameof(Progress), typeof(double), typeof(CompressionVisualizer),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RatioProperty =
        DependencyProperty.Register(nameof(Ratio), typeof(double), typeof(CompressionVisualizer),
            new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public double Ratio
    {
        get => (double)GetValue(RatioProperty);
        set => SetValue(RatioProperty, value);
    }

    private sealed class CollapseParticle
    {
        public double Angle;
        public double Dist;
        public double Speed;
        public double Size;
    }

    private readonly List<CollapseParticle> _particles = new();
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

    // Кисти заморожены: незамороженная кисть клонируется WPF при каждом обращении,
    // что в кадре анимации даёт тысячи лишних аллокаций.
    private Brush _accent = Frozen(Color.FromRgb(0, 120, 212));
    private Brush _crystal = Frozen(Color.FromRgb(138, 43, 226));

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        if (b.CanFreeze) b.Freeze();
        return b;
    }

    public CompressionVisualizer()
    {
        MinHeight = 160;
        ClipToBounds = true;

        for (int i = 0; i < 60; i++)
        {
            _particles.Add(new CollapseParticle
            {
                Angle = _rnd.NextDouble() * Math.PI * 2,
                Dist = 40 + _rnd.NextDouble() * 120,
                Speed = 40 + _rnd.NextDouble() * 90,
                Size = 1.5 + _rnd.NextDouble() * 2.0
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

        // Раньше акцент читался только в Loaded, поэтому смена темы оставляла
        // визуализатор со старым цветом до пересоздания окна.
        _themeHandler = (_, _) => RefreshThemeBrushes();
        ThemeManager.Instance.PropertyChanged += _themeHandler;
    }

    private readonly System.ComponentModel.PropertyChangedEventHandler _themeHandler;

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
            p.Dist -= dt * p.Speed;
            if (p.Dist <= 18)
            {
                p.Dist = 110 + _rnd.NextDouble() * 50;
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
        Color acc = (_accent as SolidColorBrush)?.Color ?? Color.FromRgb(0, 120, 212);

        // Раньше свойства Progress и Ratio объявлялись как DependencyProperty,
        // CreateArchiveWindow их присваивал, но OnRender их НИКОГДА не читал:
        // визуализатор показывал декоративную анимацию без всякого прогресса.
        // Теперь прогресс отражается на свечении ядра и на кольце уплотнения.
        double progress = Math.Clamp(Progress, 0, 100) / 100.0;
        double ratio = Math.Clamp(Ratio, 0, 100) / 100.0;

        // 1. Радиальное градиентное свечение ядра сжатия
        //    Яркость и размер свечения растут с прогрессом — это и есть визуальная шкала.
        byte glowAlpha = (byte)(35 + 85 * progress);
        var glow = new RadialGradientBrush(
            Color.FromArgb(glowAlpha, acc.R, acc.G, acc.B),
            Color.FromArgb(0, acc.R, acc.G, acc.B));
        if (glow.CanFreeze) glow.Freeze();
        dc.DrawEllipse(glow, null, center, 110 + 40 * progress, 80 + 26 * progress);

        // 2. Втягивающиеся частицы данных
        var pBrush = FrameBrushCache.Solid(acc, 180 / 255.0);
        var tailPen = FrameBrushCache.Pen4(acc, 80 / 255.0, 1.2);

        foreach (var p in _particles)
        {
            double x = center.X + Math.Cos(p.Angle) * p.Dist;
            double y = center.Y + Math.Sin(p.Angle) * (p.Dist * 0.65);

            double tx = center.X + Math.Cos(p.Angle) * (p.Dist + 8);
            double ty = center.Y + Math.Sin(p.Angle) * ((p.Dist + 8) * 0.65);
            dc.DrawLine(tailPen, new Point(tx, ty), new Point(x, y));

            dc.DrawEllipse(pBrush, null, new Point(x, y), p.Size, p.Size);
        }

        // 3. Вращающееся квантовое кольцо уплотнения.
        //    Раньше здесь вычислялся rot = _time * 2.2, который НЕ использовался:
        //    кольцо было неподвижным, несмотря на название в комментарии.
        double rot = _time * 2.2;
        var ringPen = FrameBrushCache.Pen4(acc, 140 / 255.0, 1.8);

        // Радиус кольца сжимается по мере роста прогресса, а сегменты вращаются.
        double ringRx = 36 - 8 * progress;
        double ringRy = 24 - 5 * progress;
        dc.PushTransform(new RotateTransform(rot, center.X, center.Y));
        dc.DrawEllipse(null, ringPen, center, ringRx, ringRy);
        dc.Pop();

        // Дуга прогресса поверх кольца: длина дуги = 360° * progress.
        if (progress > 0.001)
        {
            var arcBrush = FrameBrushCache.Solid(acc, 230 / 255.0);

            double start = -90 + rot * 0.35;
            double sweep = 360 * progress;

            // Ступенчатая дуга из коротких сегментов кругового пути.
            const int Steps = 64;
            var figure = new PathFigure { StartPoint = PointOnRing(center, ringRx, ringRy, start) };
            for (int i = 1; i <= Steps; i++)
            {
                double a = start + sweep * i / Steps;
                figure.Segments.Add(new LineSegment(PointOnRing(center, ringRx, ringRy, a), true));
            }

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            geometry.Freeze();

            var arcPen = FrameBrushCache.PenFor(arcBrush, 3.0);
            dc.DrawGeometry(null, arcPen, geometry);
        }

        // 4. Центральное ядро архива (Кристалл).
        //    Размер ядра зависит от Ratio (коэффициент сжатия) — раньше
        //    это свойство тоже не читалось и визуализатор его игнорировал.
        double coreRx = 14 + 8 * ratio;
        var coreBrush = FrameBrushCache.Solid(acc, 220 / 255.0);
        dc.DrawEllipse(coreBrush, null, center, coreRx, coreRx);

        dc.DrawEllipse(Brushes.White, null, center, coreRx / 3, coreRx / 3);
    }

    /// <summary>Точка на эллипсе под заданным углом (в градусах).</summary>
    private static Point PointOnRing(Point center, double rx, double ry, double degrees)
    {
        double rad = degrees * Math.PI / 180.0;
        return new Point(center.X + Math.Cos(rad) * rx, center.Y + Math.Sin(rad) * ry);
    }
}
