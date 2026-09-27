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

    private Brush _accent = new SolidColorBrush(Color.FromRgb(0, 120, 212));
    private Brush _crystal = new SolidColorBrush(Color.FromRgb(138, 43, 226));

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
            if (Application.Current?.Resources["AccentBrush"] is Brush b) _accent = b;
            _last = DateTime.Now;
            _running = true;
            CompositionTarget.Rendering += OnRendering;
        };
        Unloaded += (_, _) =>
        {
            _running = false;
            CompositionTarget.Rendering -= OnRendering;
        };
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_running) return;
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
        glow.Freeze();
        dc.DrawEllipse(glow, null, center, 110 + 40 * progress, 80 + 26 * progress);

        // 2. Втягивающиеся частицы данных
        var pBrush = new SolidColorBrush(Color.FromArgb(180, acc.R, acc.G, acc.B));
        pBrush.Freeze();
        var tailPen = new Pen(new SolidColorBrush(Color.FromArgb(80, acc.R, acc.G, acc.B)), 1.2);
        tailPen.Brush.Freeze();

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
        var ringPen = new Pen(new SolidColorBrush(Color.FromArgb(140, acc.R, acc.G, acc.B)), 1.8);
        ringPen.Brush.Freeze();

        // Радиус кольца сжимается по мере роста прогресса, а сегменты вращаются.
        double ringRx = 36 - 8 * progress;
        double ringRy = 24 - 5 * progress;
        dc.PushTransform(new RotateTransform(rot, center.X, center.Y));
        dc.DrawEllipse(null, ringPen, center, ringRx, ringRy);
        dc.Pop();

        // Дуга прогресса поверх кольца: длина дуги = 360° * progress.
        if (progress > 0.001)
        {
            var arcBrush = new SolidColorBrush(Color.FromArgb(230, acc.R, acc.G, acc.B));
            arcBrush.Freeze();

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

            var arcPen = new Pen(arcBrush, 3.0);
            arcPen.Freeze();
            dc.DrawGeometry(null, arcPen, geometry);
        }

        // 4. Центральное ядро архива (Кристалл).
        //    Размер ядра зависит от Ratio (коэффициент сжатия) — раньше
        //    это свойство тоже не читалось и визуализатор его игнорировал.
        double coreRx = 14 + 8 * ratio;
        var coreBrush = new SolidColorBrush(Color.FromArgb(220, acc.R, acc.G, acc.B));
        coreBrush.Freeze();
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
