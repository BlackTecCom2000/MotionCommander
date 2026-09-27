using System.Windows;
using System.Windows.Media;

namespace Win11CopyDialog.Controls;

/// <summary>График скорости копирования: сетка + заливка + линия. Данные — из CopyEngine.SpeedHistory.</summary>
public sealed class SpeedGraph : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty =
        DependencyProperty.Register(nameof(Values), typeof(IList<double>), typeof(SpeedGraph),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaxProperty =
        DependencyProperty.Register(nameof(Max), typeof(double), typeof(SpeedGraph),
            new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

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

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 10) return;

        var res = Application.Current?.Resources;
        Brush grid = (res?["GraphGridBrush"] as Brush) ?? Brushes.LightGray;
        Brush accent = (res?["AccentBrush"] as Brush) ?? Brushes.DodgerBlue;
        Brush fill = (res?["GraphFillBrush"] as Brush) ?? new SolidColorBrush(Color.FromArgb(0x55, 0, 120, 212));

        var gridPen = new Pen(grid, 1);

        // Горизонтальная сетка
        for (int i = 1; i <= 3; i++)
        {
            double y = h * i / 4;
            dc.DrawLine(gridPen, new Point(0, y), new Point(w, y));
        }

        if (Values == null || Values.Count < 2) return;

        // L-17: DefaultIfEmpty(1).Max() — мёртвый код, выше уже есть
        // ранний выход при Count < 2, а Count >= 2 гарантирует непустую коллекцию.
        double max = Math.Max(Max, Values.Max());
        if (max <= 0) max = 1;

        int n = Values.Count;

        // X-координата по ЧИСЛУ ТОЧЕК (n), а не по жёсткой константе
        // Models.CopyEngine.MaxHistory (90). Раньше ломаная сжималась в левую
        // 1/89 часть контрола, пока не набиралось 90 замеров, то есть первые
        // ~9 секунд каждой передачи (и навсегда при коротких передачах).
        double xStep = w / (n - 1);

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            for (int i = 0; i < n; i++)
            {
                double x = i * xStep;
                double y = h - 4 - (h - 10) * (Values[i] / max);
                y = Math.Clamp(y, 2, h - 2);
                if (i == 0) ctx.BeginFigure(new Point(x, y), false, false);
                else ctx.LineTo(new Point(x, y), true, false);
            }
        }
        line.Freeze();

        // Заливка под линией
        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(0, h), true, true);
            for (int i = 0; i < n; i++)
            {
                double x = i * xStep;
                double y = h - 4 - (h - 10) * (Values[i] / max);
                ctx.LineTo(new Point(x, Math.Clamp(y, 2, h - 2)), true, false);
            }
            ctx.LineTo(new Point(w, h), true, false);
        }
        area.Freeze();
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, new Pen(accent, 2) { LineJoin = PenLineJoin.Round }, line);
    }
}
