using System.Windows;
using System.Windows.Media;
using Win11CopyDialog.Helpers;
using Win11CopyDialog.Models;

namespace Win11CopyDialog.Controls;

/// <summary>
/// Real-time waveform скорости: сглаженные кривые (Catmull-Rom → Bezier),
/// градиентная заливка, светящаяся голова, пульс. История — последние N замеров.
/// </summary>
public sealed class WaveformGraph : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty =
        DependencyProperty.Register(nameof(Values), typeof(IList<double>), typeof(WaveformGraph),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaxProperty =
        DependencyProperty.Register(nameof(Max), typeof(double), typeof(WaveformGraph),
            new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CapacityProperty =
        DependencyProperty.Register(nameof(Capacity), typeof(int), typeof(WaveformGraph),
            new FrameworkPropertyMetadata(60, FrameworkPropertyMetadataOptions.AffectsRender));

    public IList<double>? Values
    {
        get => (IList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public double Max
    {
        get => (double)GetValue(MaxProperty);
        set => SetValue(MaxProperty, value);
    }

    public int Capacity
    {
        get => (int)GetValue(CapacityProperty);
        set => SetValue(CapacityProperty, value);
    }

    private double _time;
    private DateTime _last = DateTime.Now;
    private bool _running;
    private Brush _accent = new SolidColorBrush(Color.FromRgb(0, 120, 212));
    private Brush _grid = new SolidColorBrush(Color.FromRgb(227, 227, 227));

    public WaveformGraph()
    {
        MinHeight = 70;
        Loaded += (_, _) =>
        {
            RefreshBrushes();
            ThemeManager.Instance.PropertyChanged += OnThemeChanged;
            _last = DateTime.Now;
            _running = true;
            CompositionTarget.Rendering += OnRendering;
        };
        Unloaded += (_, _) =>
        {
            _running = false;
            CompositionTarget.Rendering -= OnRendering;
            ThemeManager.Instance.PropertyChanged -= OnThemeChanged;
        };
    }

    private void OnThemeChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e) =>
        Dispatcher.Invoke(RefreshBrushes);

    private void RefreshBrushes()
    {
        var r = Application.Current?.Resources;
        if (r == null) return;
        if (r["AccentBrush"] is Brush a) _accent = a;
        if (r["GraphGridBrush"] is Brush g) _grid = g;
    }

    private void OnRendering(object? s, EventArgs e)
    {
        if (!_running) return;
        var now = DateTime.Now;
        _time += Math.Min(0.05, (now - _last).TotalSeconds);
        _last = now;
        InvalidateVisual(); // голова пульсирует непрерывно
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth, h = ActualHeight;
        if (w < 20 || h < 20) return;

        var gridPen = new Pen(_grid, 1);
        gridPen.Freeze();
        for (int i = 1; i <= 3; i++)
        {
            double y = h * i / 4;
            dc.DrawLine(gridPen, new Point(0, y), new Point(w, y));
        }

        if (Values == null || Values.Count < 2) return;
        var ac = ((SolidColorBrush)_accent).Color;

        int cap = Math.Max(8, Capacity);
        int skip = Math.Max(0, Values.Count - cap);
        int n = Values.Count - skip;
        if (n < 2) return;

        double max = Math.Max(Max, 1);
        for (int i = skip; i < Values.Count; i++)
            max = Math.Max(max, Values[i]);

        var pts = new Point[n];
        for (int i = 0; i < n; i++)
        {
            double x = w * i / (cap - 1);
            double v = Motion.Clamp01(Values[skip + i] / max);
            pts[i] = new Point(x, h - 5 - (h - 12) * v);
        }

        // сглаженная кривая Catmull-Rom → Bezier
        var line = new PathGeometry();
        var fig = new PathFigure { StartPoint = pts[0], IsClosed = false };
        for (int i = 0; i < n - 1; i++)
        {
            Point p0 = pts[Math.Max(0, i - 1)], p1 = pts[i], p2 = pts[i + 1], p3 = pts[Math.Min(n - 1, i + 2)];
            fig.Segments.Add(new BezierSegment(
                new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6),
                new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6),
                p2, true));
        }
        line.Figures.Add(fig);
        line.Freeze();

        // заливка
        var area = new PathGeometry();
        var afig = new PathFigure { StartPoint = new Point(pts[0].X, h), IsClosed = true };
        afig.Segments.Add(new PolyLineSegment(pts, true));
        afig.Segments.Add(new LineSegment(new Point(pts[n - 1].X, h), true));
        area.Figures.Add(afig);
        area.Freeze();
        var fillGrad = new LinearGradientBrush(
            Color.FromArgb(90, ac.R, ac.G, ac.B), Color.FromArgb(4, ac.R, ac.G, ac.B),
            new Point(0, 0), new Point(0, 1));
        fillGrad.Freeze();
        dc.DrawGeometry(fillGrad, null, area);

        var pen = new Pen(_accent, 2) { LineJoin = PenLineJoin.Round };
        dc.DrawGeometry(null, pen, line);

        // светящаяся голова
        var head = pts[n - 1];
        double pulse = 0.6 + 0.4 * Math.Sin(_time * 5);
        var halo = new SolidColorBrush(Color.FromArgb((byte)(70 * pulse), ac.R, ac.G, ac.B));
        halo.Freeze();
        dc.DrawEllipse(halo, null, head, 8, 8);
        dc.DrawEllipse(_accent, null, head, 3.4, 3.4);
        var core = new SolidColorBrush(Colors.White);
        core.Freeze();
        dc.DrawEllipse(core, null, head, 1.4, 1.4);
    }
}
