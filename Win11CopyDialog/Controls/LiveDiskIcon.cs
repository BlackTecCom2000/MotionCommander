using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using Win11CopyDialog.Helpers;
using Win11CopyDialog.Models;

namespace Win11CopyDialog.Controls;

/// <summary>
/// Модель «живой» иконки накопителя. Питается РЕАЛЬНЫМИ данными
/// телеметрии диска, а не декоративной анимацией.
/// </summary>
public sealed class LiveDiskIconModel : INotifyPropertyChanged
{
    private double _activity;          // 0..1, доля времени накопителя занята
    private double _readMBps;
    private double _writeMBps;
    private bool _hasTelemetry;

    /// <summary>
    /// Доля занятости накопителя, 0..1.
    /// <para>Берётся из реального PercentDiskTime. При нулевой нагрузке
    /// иконка НЕ вращается: молчащий диск не должен выглядеть работающим.</para>
    /// </summary>
    public double Activity
    {
        get => _activity;
        set { if (Math.Abs(_activity - value) < 0.004) return; _activity = value; OnChanged(); }
    }

    /// <summary>Скорость чтения, МБ/с (реальные данные).</summary>
    public double ReadMBps
    {
        get => _readMBps;
        set { if (Math.Abs(_readMBps - value) < 0.05) return; _readMBps = value; OnChanged(); OnChanged(nameof(TotalMBps)); }
    }

    /// <summary>Скорость записи, МБ/с (реальные данные).</summary>
    public double WriteMBps
    {
        get => _writeMBps;
        set { if (Math.Abs(_writeMBps - value) < 0.05) return; _writeMBps = value; OnChanged(); OnChanged(nameof(TotalMBps)); }
    }

    /// <summary>Суммарная пропускная способность, МБ/с.</summary>
    public double TotalMBps => _readMBps + _writeMBps;

    /// <summary>Есть ли хоть какие-то измеренные данные.</summary>
    public bool HasTelemetry
    {
        get => _hasTelemetry;
        set { if (_hasTelemetry == value) return; _hasTelemetry = value; OnChanged(); }
    }

    /// <summary>Принимает реальные значения из StorageMonitorService.</summary>
    public void Update(double readMBps, double writeMBps, double activeTimePercent)
    {
        HasTelemetry = true;
        ReadMBps = readMBps;
        WriteMBps = writeMBps;
        Activity = Math.Clamp(activeTimePercent / 100.0, 0.0, 1.0);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>
/// Иконка накопителя, «оживающая» по фактической нагрузке.
///
/// <para><b>Что здесь честного.</b> Вращение кольца задаётся реальной
/// долей занятости диска (PercentDiskTime), а яркость направлений чтения и
/// записи — реальными скоростями. Если накопитель простаивает, иконка
/// неподвижна: движение означало бы, что диск работает, а это неправда.</para>
///
/// <para><b>Производительность.</b> Анимация идёт только когда есть
/// нагрузка; кисти берутся из кэша (Helpers.FrameBrushCache), поэтому в
/// кадре нет аллокаций. При нулевой активности кадры не выдаются вовсе,
/// и фоновое потребление равно нулю.</para>
/// </summary>
public sealed class LiveDiskIcon : FrameworkElement
{
    private readonly LiveDiskIconModel _model = new();
    private double _phase;
    private DateTime _last = DateTime.Now;
    private bool _running;

    public LiveDiskIcon()
    {
        IsHitTestVisible = false;
        _model.PropertyChanged += (_, _) => InvalidateVisual();
        Loaded += (_, _) =>
        {
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

    /// <summary>Модель с реальными данными телеметрии.</summary>
    public LiveDiskIconModel Model => _model;

    /// <summary>Размер иконки в DIP.</summary>
    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(
        nameof(IconSize), typeof(double), typeof(LiveDiskIcon),
        new FrameworkPropertyMetadata(28.0));

    /// <summary>Показывать направления чтения/записи.</summary>
    public bool ShowFlow
    {
        get => (bool)GetValue(ShowFlowProperty);
        set => SetValue(ShowFlowProperty, value);
    }

    public static readonly DependencyProperty ShowFlowProperty = DependencyProperty.Register(
        nameof(ShowFlow), typeof(bool), typeof(LiveDiskIcon), new FrameworkPropertyMetadata(true));

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_running) return;

        // Не выдавать кадры, если иконка не видна или накопитель простаивает.
        if (!IsVisible || Visibility != Visibility.Visible)
        {
            StopLoop();
            return;
        }

        if (_model.Activity <= 0.001)
        {
            // Нагрузки нет: останавливаем цикл, чтобы не жечь CPU впустую.
            StopLoop();
            return;
        }

        // В режиме «Эконом» — вдвое меньше кадров.
        if (ThemeManager.Instance.AnimationQuality == AnimationQuality.Economy)
        {
            if ((DateTime.Now.Ticks / 166666) % 2 == 0) return;
        }

        var now = DateTime.Now;
        double dt = Math.Min(0.05, (now - _last).TotalSeconds);
        _last = now;

        // Скорость вращения пропорциональна реальной загрузке диска.
        _phase = (_phase + dt * _model.Activity * 4.2) % (Math.PI * 2);
        InvalidateVisual();
    }

    private void StopLoop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnRendering;
        _last = DateTime.Now;
    }

    private void EnsureLoop()
    {
        if (_running) return;
        _running = true;
        _last = DateTime.Now;
        CompositionTarget.Rendering += OnRendering;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double s = IconSize;
        if (s <= 4) return;

        var accent = ResolveBrush("AccentBrush", Color.FromRgb(0x16, 0x8C, 0xFF));
        var track = ResolveBrush("ProgressTrackBrush", Color.FromRgb(0x26, 0x2D, 0x3D));
        var success = ResolveBrush("StatusSuccessBrush", Color.FromRgb(0x00, 0xD6, 0x8F));
        var danger = ResolveBrush("StatusDangerBrush", Color.FromRgb(0xFF, 0x45, 0x67));

        var center = new Point(s / 2, s / 2);
        double radius = s / 2 - 1.5;

        // 1. Кольцо занятости. Длина дуги = реальная доля занятости диска.
        var ringTrack = FrameBrushCache.Pen4(ColorOf(track), 1.0, 2.0);
        dc.DrawEllipse(null, ringTrack, center, radius, radius);

        double act = _model.Activity;
        if (act > 0.001)
        {
            // Цвет кольца зависит от реальной нагрузки: норма / внимание / перегрузка.
            Color ringColor = act >= 0.9 ? ColorOf(danger)
                            : act >= 0.6 ? ColorOf(accent)
                            : ColorOf(success);

            var ring = FrameBrushCache.Pen4(ringColor, 0.35 + act * 0.65, 2.0);

            // У DrawingContext НЕТ метода DrawArc (он есть у Graphics только).
            // Дуга строится как геометрия и кэшируется по паре
            // (радиус в целых пикселях, процент занятости), поэтому в кадре
            // не происходит ни одной аллокации.
            int pct = (int)Math.Round(Math.Clamp(act, 0, 1) * 100);
            var arc = ArcCache.Get((int)Math.Round(radius), pct);
            if (arc != null)
                dc.DrawGeometry(null, ring, arc);

            // 2. Вращающийся бегунок на кольце — видно движение только под нагрузкой.
            EnsureLoop();
            double ang = _phase * 180 / Math.PI;
            var pt = new Point(
                center.X + Math.Cos(ang * Math.PI / 180) * radius,
                center.Y + Math.Sin(ang * Math.PI / 180) * radius);
            var dot = FrameBrushCache.Solid(ringColor, 0.55 + act * 0.45);
            dc.DrawEllipse(dot, null, pt, 1.6 + act * 1.4, 1.6 + act * 1.4);
        }
        else
        {
            // Накопитель простаивает: индикатор отсутствует, а не мигает.
            StopLoop();
        }

        // 3. Корпус накопителя.
        var bodyBrush = FrameBrushCache.Pen4(ColorOf(accent), 0.75, 1.4);
        double w = s * 0.42, h = s * 0.26;
        dc.DrawRoundedRectangle(null, bodyBrush,
            new Rect(center.X - w / 2, center.Y - h / 2, w, h), 2, 2);

        // 4. Направления потока: длина и яркость = реальные скорости.
        if (ShowFlow && _model.HasTelemetry && _model.TotalMBps > 0)
        {
            double scale = 1.0 - Math.Exp(-_model.TotalMBps / 25.0);   // 0..1, насыщается
            double arrowY = center.Y + h / 2 + 1.6;
            double armW = w * 0.30;

            // Стрелка чтения — влево, реальная скорость чтения.
            DrawArrow(dc, new Point(center.X - armW, arrowY), new Point(center.X - 1, arrowY),
                _model.ReadMBps, scale, accent);
            // Стрелка записи — вправо, реальная скорость записи.
            DrawArrow(dc, new Point(center.X + 1, arrowY), new Point(center.X + armW, arrowY),
                _model.WriteMBps, scale, accent);
        }
    }

    private static void DrawArrow(DrawingContext dc, Point from, Point to,
        double mbps, double scale, Brush accent)
    {
        if (mbps <= 0.01) return;   // нет потока — стрелки нет

        Color c = ColorOf(accent);
        var pen = FrameBrushCache.Pen4(c, 0.35 + scale * 0.65, 1.2);
        dc.DrawLine(pen, from, to);

        // Наконечник
        double dir = to.X >= from.X ? 1 : -1;
        var head = FrameBrushCache.Solid(c, 0.45 + scale * 0.55);
        var tip = new Point(to.X, to.Y);
        dc.DrawLine(pen, tip, new Point(to.X - dir * 2.4, to.Y - 1.8));
        dc.DrawLine(pen, tip, new Point(to.X - dir * 2.4, to.Y + 1.8));
        dc.DrawEllipse(head, null, tip, 1.0, 1.0);
    }

    private Brush ResolveBrush(string key, Color fallback)
    {
        var res = Application.Current?.Resources;
        if (res != null)
        {
            object? v = UiDispatcher.LookupResource(res, key);
            if (v is Brush b) return b;
        }
        return FrameBrushCache.Frozen(fallback);
    }

    private static Color ColorOf(Brush b)
        => b is SolidColorBrush scb ? scb.Color : Colors.Gray;
}

/// <summary>
/// Кэш геометрии дуг для <see cref="LiveDiskIcon"/>.
/// </summary>
/// <remarks>
/// <para>Дуга строится один раз на пару (радиус, процент занятости) и
/// замораживается. Ключ квантуется: радиус — до целых пикселей, занятость —
/// до 1 % (100 значений). При реальной телеметрии это десятки записей,
/// а не рост памяти с частотой кадров.</para>
/// </remarks>
internal static class ArcCache
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), Geometry> _cache = new();

    public static Geometry? Get(int radius, int percent)
    {
        if (radius < 1) return null;
        percent = Math.Clamp(percent, 0, 100);
        if (percent == 0) return null;

        var key = (radius, percent);
        if (_cache.TryGetValue(key, out var hit)) return hit;

        try
        {
            double sweep = percent * 360.0 / 100.0;
            // Полный оборот строить нельзя: у эллипса начало и конец
            // совпадают, и такая геометрия рисуется некорректно.
            if (sweep >= 359.5) sweep = 359.5;

            var startDeg = -90.0;
            var sweepRad = sweep * Math.PI / 180.0;
            double sx = Math.Cos(startDeg * Math.PI / 180.0) * radius;
            double sy = Math.Sin(startDeg * Math.PI / 180.0) * radius;
            double ex = Math.Cos((startDeg + sweep) * Math.PI / 180.0) * radius;
            double ey = Math.Sin((startDeg + sweep) * Math.PI / 180.0) * radius;

            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(sx, sy), false, false);
                // У StreamGeometryContext.ArcTo есть только перегрузка
                // с isLargeArc и isSmoothJoin: 4-й аргумент — bool,
                // а не SweepDirection. С углом разворота больше полуоборота
                // дуга должна быть «большой», иначе WPF рисует короткую.
                bool isLargeArc = sweep > 180.0;
                ctx.ArcTo(
                    new Point(ex, ey),
                    new Size(radius, radius),
                    0,
                    isLargeArc,
                    SweepDirection.Clockwise,
                    true,
                    false);
            }
            g.Freeze();

            return _cache.GetOrAdd(key, g);
        }
        catch
        {
            // Геометрия не построилась — иконка покажет кольцо без дуги,
            // что честнее, чем падение отрисовки на пустом элементе.
            return null;
        }
    }
}
