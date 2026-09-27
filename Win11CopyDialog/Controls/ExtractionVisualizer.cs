using System.Windows;
using System.Windows.Media;
using Win11CopyDialog.Helpers;

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

    private Brush _accent = new SolidColorBrush(Color.FromRgb(16, 185, 129));

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
        glow.Freeze();
        dc.DrawEllipse(glow, null, center, 120, 80);

        // 2. Расширяющиеся концентрические волны
        double wavePhase = (_time * 1.8) % 1.0;
        var wavePen = new Pen(new SolidColorBrush(Color.FromArgb((byte)((1 - wavePhase) * 120), acc.R, acc.G, acc.B)), 1.5);
        wavePen.Brush.Freeze();
        dc.DrawEllipse(null, wavePen, center, 20 + wavePhase * 80, 15 + wavePhase * 55);

        // 3. Вылетающие наружу частицы файлов со шлейфами.
        //    Начало шлейфа отодвигается по мере роста прогресса — визуальный отклик.
        double tailOffset = 12 - 6 * progress;
        var pBrush = new SolidColorBrush(Color.FromArgb(200, acc.R, acc.G, acc.B));
        pBrush.Freeze();
        var tailPen = new Pen(new SolidColorBrush(Color.FromArgb(90, acc.R, acc.G, acc.B)), 1.4);
        tailPen.Brush.Freeze();

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

            var trackPen = new Pen(new SolidColorBrush(Color.FromArgb(50, acc.R, acc.G, acc.B)), 3.0);
            trackPen.Brush.Freeze();
            dc.DrawEllipse(null, trackPen, center, rx, ry);

            var arcBrush = new SolidColorBrush(Color.FromArgb(235, acc.R, acc.G, acc.B));
            arcBrush.Freeze();
            var arcPen = new Pen(arcBrush, 3.0);
            arcPen.Freeze();
            dc.DrawGeometry(null, arcPen, geometry);
        }

        // 5. Центральное раскрывающеесь ядро архива
        var coreBrush = new SolidColorBrush(Color.FromArgb(220, acc.R, acc.G, acc.B));
        coreBrush.Freeze();
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
