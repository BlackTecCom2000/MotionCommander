using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Win11CopyDialog.Helpers;
using Win11CopyDialog.Models;

namespace Win11CopyDialog.Controls;

/// <summary>
/// Футуристический Hero-визуализатор космической передачи данных (Space / Hologram Telemetry):
/// - 3D Starfield & Parallax: звёздное поле глубокого космоса с интерактивным параллаксом от мыши;
/// - Quantum Hyperspace Conduit: двойная спиральная квантовая волна (3D helix wave);
/// - Data Comet Stream: частицы данных со светящимися кометами-хвостами;
/// - Sci-Fi HUD Reticles: вращающиеся компасные кольца узлов SRC/DST с радаром;
/// - Central Celestial Ring: неоновое кольцо прогресса с фотонным маяком и градусными рисками;
/// - Quantum Burst: расширяющийся космический импульс и фотонные вспышки при завершении.
public enum TransferState { Preparing, Copying, Paused, Completed, Error }

public sealed class TransferVisualizer : FrameworkElement
{
    public const int MaxParticles = 130;

    public static readonly DependencyProperty ProgressProperty =
        DependencyProperty.Register(nameof(Progress), typeof(double), typeof(TransferVisualizer),
            new FrameworkPropertyMetadata(0.0));

    public static readonly DependencyProperty SpeedNormProperty =
        DependencyProperty.Register(nameof(SpeedNorm), typeof(double), typeof(TransferVisualizer),
            new FrameworkPropertyMetadata(0.0));

    public static readonly DependencyProperty StateProperty =
        DependencyProperty.Register(nameof(State), typeof(TransferState), typeof(TransferVisualizer),
            new FrameworkPropertyMetadata(TransferState.Preparing,
                FrameworkPropertyMetadataOptions.AffectsRender, OnStateChanged));

    public static readonly DependencyProperty SourceLabelProperty =
        DependencyProperty.Register(nameof(SourceLabel), typeof(string), typeof(TransferVisualizer),
            new FrameworkPropertyMetadata("Источник", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DestLabelProperty =
        DependencyProperty.Register(nameof(DestLabel), typeof(string), typeof(TransferVisualizer),
            new FrameworkPropertyMetadata("Приёмник", FrameworkPropertyMetadataOptions.AffectsRender));

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public double SpeedNorm
    {
        get => (double)GetValue(SpeedNormProperty);
        set => SetValue(SpeedNormProperty, value);
    }

    public TransferState State
    {
        get => (TransferState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public string SourceLabel
    {
        get => (string)GetValue(SourceLabelProperty);
        set => SetValue(SourceLabelProperty, value);
    }

    public string DestLabel
    {
        get => (string)GetValue(DestLabelProperty);
        set => SetValue(DestLabelProperty, value);
    }

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var v = (TransferVisualizer)d;
        v.OnEnterState((TransferState)e.NewValue);
    }

    // --- Структуры данных анимации ---
    private sealed class Star
    {
        public double X, Y, Z, Speed, TwinkleRate;
    }

    private sealed class Particle { public double T; public double Lane; public double Jitter; public double Size; }
    private sealed class BurstBit { public Point P; public Vector V; public double Life; }

    private readonly List<Star> _stars = new();
    private readonly List<Particle> _particles = new();
    private readonly List<BurstBit> _bursts = new();
    private readonly Random _rnd = new();
    private DateTime _last = DateTime.Now;
    private double _time;
    private double _energy;
    private double _spawnAcc;
    private double _pulseT = -1;
    private double _errorGlow;
    private bool _running;

    // Интерактивный 3D-параллакс пространства
    private double _targetParallaxX;
    private double _targetParallaxY;
    private double _parallaxX;
    private double _parallaxY;

    // Кэшированные кисти. Все заморожены: незамороженная кисть клонируется WPF
    // при каждом обращении, а в кадре это сотни аллокаций.
    private Brush _accent = FrameBrushCache.Frozen(Color.FromRgb(0, 120, 212));
    private Brush _track = FrameBrushCache.Frozen(Color.FromRgb(227, 227, 227));
    private Brush _card = FrameBrushCache.Frozen(Color.FromRgb(255, 255, 255));
    private Brush _secondary = FrameBrushCache.Frozen(Color.FromRgb(96, 94, 92));
    private Brush _success = FrameBrushCache.Frozen(Color.FromRgb(16, 124, 16));
    private Brush _warning = FrameBrushCache.Frozen(Color.FromRgb(234, 163, 0));

    // Перья, зависящие только от _track: пересоздавать их в кадре незачем.
    private Pen _ringPen = MakePen(FrameBrushCache.Frozen(Color.FromRgb(227, 227, 227)), 7);
    private Pen _nodeRingPen = MakePen(FrameBrushCache.Frozen(Color.FromRgb(227, 227, 227)), 1.5);
    private Pen _flowPen = MakePen(FrameBrushCache.Frozen(Color.FromRgb(0, 120, 212)), 2.5, PenLineCap.Round, PenLineJoin.Round);
    private Pen _flowPen7 = MakePen(FrameBrushCache.Frozen(Color.FromRgb(0, 120, 212)), 7, PenLineCap.Round);
    private Pen _trackPen3 = MakePen(FrameBrushCache.Frozen(Color.FromRgb(227, 227, 227)), 3, PenLineCap.Flat, PenLineJoin.Round);

    private static Pen MakePen(Brush b, double thickness, PenLineCap cap = PenLineCap.Flat, PenLineJoin join = PenLineJoin.Miter)
    {
        var p = new Pen(b, thickness) { StartLineCap = cap, EndLineCap = cap, LineJoin = join };
        if (p.CanFreeze) p.Freeze();
        return p;
    }

    public TransferVisualizer()
    {
        MinHeight = 230;
        ClipToBounds = true;

        // Генерация 48 космических звёзд глубокого пространства
        for (int i = 0; i < 48; i++)
        {
            _stars.Add(new Star
            {
                X = _rnd.NextDouble(),
                Y = _rnd.NextDouble(),
                Z = 0.2 + _rnd.NextDouble() * 0.8,
                Speed = 0.005 + _rnd.NextDouble() * 0.015,
                TwinkleRate = 1.5 + _rnd.NextDouble() * 3.0
            });
        }

        MouseMove += OnMouseMove;
        MouseLeave += OnMouseLeave;

        Loaded += (_, _) =>
        {
            RefreshBrushes();
            ThemeManager.Instance.PropertyChanged += OnThemeChanged;
            _last = DateTime.Now;
            _running = true;
            CompositionTarget.Rendering += OnRendering;
            // Раньше анимация крутилась всегда, даже когда окно свёрнуто или
            // вкладка скрыта: 60 FPS впустую на фоне. Теперь рендер идёт
            // только когда элемент действительно нарисован.
            IsVisibleChanged += OnVisibilityChanged;
        };
        Unloaded += (_, _) =>
        {
            _running = false;
            CompositionTarget.Rendering -= OnRendering;
            ThemeManager.Instance.PropertyChanged -= OnThemeChanged;
            IsVisibleChanged -= OnVisibilityChanged;
        };
    }

    /// <summary>Окно свёрнуто или элемент скрыт — рендер тратит CPU впустую.</summary>
    private bool ShouldRender =>
        IsVisible && IsLoaded && Visibility == Visibility.Visible &&
        Window.GetWindow(this) is { WindowState: not WindowState.Minimized, IsVisible: true };

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _last = DateTime.Now;   // не накапливать dt за время скрытия
        if (ShouldRender) InvalidateVisual();
    }

    /// <summary>Режим «Эконом»: 30 FPS вместо 60 вдвое снижает нагрузку без заметной потери плавности.</summary>
    private bool _skipFrame;

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var pt = e.GetPosition(this);
        double normX = (pt.X / Math.Max(1, ActualWidth)) * 2 - 1; // -1..1
        double normY = (pt.Y / Math.Max(1, ActualHeight)) * 2 - 1;
        _targetParallaxX = normX * 16;
        _targetParallaxY = normY * 10;
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        _targetParallaxX = 0;
        _targetParallaxY = 0;
    }

    private void OnThemeChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null || e.PropertyName.Contains("Accent") || e.PropertyName.Contains("Dark"))
            Dispatcher.Invoke(RefreshBrushes);
    }

    private void RefreshBrushes()
    {
        var r = Application.Current?.Resources;
        if (r == null) return;
        if (Lookup(r, "AccentBrush") is Brush a) _accent = a;
        if (Lookup(r, "GraphGridBrush") is Brush g) _track = g;
        if (Lookup(r, "CardBackgroundBrush") is Brush c) _card = c;
        if (Lookup(r, "SecondaryTextBrush") is Brush s) _secondary = s;

        // Перья зависят от акцента и трека — пересобираем после смены темы.
        _ringPen = MakePen(_track, 7);
        _nodeRingPen = MakePen(_track, 1.5);
        _trackPen3 = MakePen(_track, 3, PenLineCap.Flat, PenLineJoin.Round);
        _flowPen = MakePen(_accent, 2.5, PenLineCap.Round, PenLineJoin.Round);
        _flowPen7 = MakePen(_accent, 7, PenLineCap.Round);

        // Кисти старой темы больше не нужны.
        FrameBrushCache.Trim();
        FrameTextCache.Trim();
        InvalidateVisual();
    }

    /// <summary>
    /// Поиск по всей цепочке словарей. Application.Resources["Key"] НЕ ищет в
    /// объединённых словарях, поэтому часть токенов молча не находилась.
    /// </summary>
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

    private void OnEnterState(TransferState st)
    {
        if (st == TransferState.Completed)
        {
            _pulseT = 0;
            var c = new Point(ActualWidth / 2, ActualHeight / 2);
            for (int i = 0; i < 70; i++)
            {
                double ang = _rnd.NextDouble() * Math.PI * 2;
                double sp = 60 + _rnd.NextDouble() * 220;
                _bursts.Add(new BurstBit
                {
                    P = c,
                    V = new Vector(Math.Cos(ang) * sp, Math.Sin(ang) * sp),
                    Life = 1
                });
            }
        }
        else if (st == TransferState.Error)
        {
            _errorGlow = 1;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_running) return;

        // В свёрнутом окне или на скрытой вкладке кадры не нужны.
        if (!ShouldRender) return;

        // Режим «Эконом»: рендерим каждый второй кадр (~30 FPS).
        if (ThemeManager.Instance.AnimationQuality == AnimationQuality.Economy)
        {
            _skipFrame = !_skipFrame;
            if (_skipFrame) return;
        }

        var now = DateTime.Now;
        double dt = Math.Min(0.05, (now - _last).TotalSeconds);
        _last = now;
        _time += dt;

        // Плавный 3D параллакс
        _parallaxX = Motion.Damp(_parallaxX, _targetParallaxX, 4, dt);
        _parallaxY = Motion.Damp(_parallaxY, _targetParallaxY, 4, dt);

        // Дрейф звёзд космического поля
        foreach (var st in _stars)
        {
            st.X += dt * st.Speed * st.Z;
            if (st.X > 1.0) st.X -= 1.0;
        }

        // Целевая энергия по состоянию
        double target = State switch
        {
            TransferState.Copying => 0.16 + 0.84 * Motion.Clamp01(SpeedNorm),
            TransferState.Preparing => 0.12,
            TransferState.Completed => 0.04,
            _ => 0.0
        };
        double rate = target > _energy ? 1.8 : 3.2;
        _energy = Motion.Damp(_energy, target, rate, dt);

        // Спавн частиц
        _spawnAcc += _energy * 95 * dt;
        while (_spawnAcc >= 1 && _particles.Count < MaxParticles)
        {
            _spawnAcc -= 1;
            _particles.Add(new Particle
            {
                T = 0,
                Lane = _rnd.NextDouble() * 2 - 1,
                Jitter = _rnd.NextDouble(),
                Size = 1.6 + _rnd.NextDouble() * 1.6
            });
        }
        if (_spawnAcc > 4) _spawnAcc = 4;

        double flow = 0.20 + _energy * 0.95;
        for (int i = _particles.Count - 1; i >= 0; i--)
        {
            var p = _particles[i];
            p.T += dt * flow * (0.75 + p.Jitter * 0.5);
            if (p.T >= 1)
            {
                if (_energy > 0.02 && _particles.Count <= MaxParticles)
                {
                    p.T = 0; p.Lane = _rnd.NextDouble() * 2 - 1;
                    p.Jitter = _rnd.NextDouble(); p.Size = 1.6 + _rnd.NextDouble() * 1.6;
                }
                else _particles.RemoveAt(i);
            }
        }

        // Burst-частицы завершения
        for (int i = _bursts.Count - 1; i >= 0; i--)
        {
            var b = _bursts[i];
            b.Life -= dt / 0.9;
            if (b.Life <= 0) { _bursts.RemoveAt(i); continue; }
            b.V *= 1 - 1.6 * dt;
            b.P += b.V * dt;
        }

        if (_pulseT >= 0)
        {
            _pulseT += dt / 0.9;
            if (_pulseT > 1.4) _pulseT = -1;
        }
        if (_errorGlow > 0) _errorGlow = Math.Max(0, _errorGlow - dt / 1.2);

        InvalidateVisual();
    }

    private void FlowPath(double w, double h, out Point p0, out Point p1, out Point p2, out Point p3)
    {
        double y = h / 2;
        double x0 = 64, x1 = w - 64;
        p0 = new Point(x0, y);
        p1 = new Point(x0 + (x1 - x0) * 0.32, y - 44);
        p2 = new Point(x0 + (x1 - x0) * 0.68, y + 44);
        p3 = new Point(x1, y);
    }

    private static Point Cubic(Point p0, Point p1, Point p2, Point p3, double t)
    {
        double u = 1 - t;
        return new Point(
            u * u * u * p0.X + 3 * u * u * t * p1.X + 3 * u * t * t * p2.X + t * t * t * p3.X,
            u * u * u * p0.Y + 3 * u * u * t * p1.Y + 3 * u * t * t * p2.Y + t * t * t * p3.Y);
    }

    private static Vector CubicTangent(Point p0, Point p1, Point p2, Point p3, double t)
    {
        double u = 1 - t;
        var v = new Vector(
            3 * u * u * (p1.X - p0.X) + 6 * u * t * (p2.X - p1.X) + 3 * t * t * (p3.X - p2.X),
            3 * u * u * (p1.Y - p0.Y) + 6 * u * t * (p2.Y - p1.Y) + 3 * t * t * (p3.Y - p2.Y));
        v.Normalize();
        return v;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth, h = ActualHeight;
        if (w < 60 || h < 60) return;

        bool done = State == TransferState.Completed;
        Brush flowBrush = done ? _success : _accent;
        Color accColor = ((SolidColorBrush)flowBrush).Color;
        var center = new Point(w / 2, h / 2);

        // 1. КОСМИЧЕСКОЕ ЗВЁЗДНОЕ ПОЛЕ С 3D-ПАРАЛЛАКСОМ (Starfield Space Depth)
        // Кисти берутся из кэша с квантованной прозрачностью. Раньше здесь
        // создавалось и замораживалось 48 кистей на КАЖДЫЙ кадр.
        foreach (var star in _stars)
        {
            double sx = star.X * w + _parallaxX * star.Z;
            double sy = star.Y * h + _parallaxY * star.Z;
            if (sx < 0) sx += w; if (sx > w) sx -= w;
            if (sy < 0) sy += h; if (sy > h) sy -= h;

            double twinkle = 0.25 + 0.75 * Math.Abs(Math.Sin(_time * star.TwinkleRate));
            double alpha = twinkle * star.Z * (120 / 255.0);
            var starBrush = FrameBrushCache.Solid(accColor, alpha);
            double r = 0.9 + star.Z * 1.1;
            dc.DrawEllipse(starBrush, null, new Point(sx, sy), r, r);
        }

        // 2. ГЛУБОКАЯ КОСМИЧЕСКАЯ НЕОНОВАЯ ВИЗУАЛИЗАЦИЯ (Cosmic Nebula Core)
        var vignette = new RadialGradientBrush(
            Color.FromArgb((byte)(24 + _energy * 30), accColor.R, accColor.G, accColor.B),
            Color.FromArgb(0, accColor.R, accColor.G, accColor.B));
        if (vignette.CanFreeze) vignette.Freeze();
        dc.DrawEllipse(vignette, null, center, w * 0.48, h * 0.58);

        // 3. ТОНКАЯ КИБЕР-СЕТКА И ПРИЦЕЛЫ (Sci-Fi Coordinates)
        var hudPen = FrameBrushCache.Pen4(accColor, 0x18 / 255.0, 1);
        dc.DrawLine(hudPen, new Point(30, 20), new Point(30, 36));
        dc.DrawLine(hudPen, new Point(22, 28), new Point(38, 28));
        dc.DrawLine(hudPen, new Point(w - 30, 20), new Point(w - 30, 36));
        dc.DrawLine(hudPen, new Point(w - 38, 28), new Point(w - 22, 28));

        // 4. ТРАЕКТОРИЯ ПОТОКА ДАННЫХ
        FlowPath(w, h, out var p0, out var p1, out var p2, out var p3);
        var path = new StreamGeometry();
        using (var ctx = path.Open())
        {
            ctx.BeginFigure(p0, false, false);
            ctx.BezierTo(p1, p2, p3, true, false);
        }
        path.Freeze();

        // Базовый трек
        dc.DrawGeometry(null, _trackPen3, path);

        // 5. КВАНТОВЫЙ ГИПЕРПРОСТРАНСТВЕННЫЙ ТОННЕЛЬ (3D Quantum Helix Conduit)
        if (_energy > 0.02)
        {
            // Неоновое свечение основного пути
            var glowPen = FrameBrushCache.Pen4(accColor, (40 + _energy * 70) / 255.0, 9 + _energy * 4, PenLineCap.Flat, PenLineJoin.Round);
            dc.DrawGeometry(null, glowPen, path);
            dc.DrawGeometry(null, _flowPen, path);

            // Двойная спиральная волна гиперпространственного туннеля (Quantum Helix)
            var helixGeom1 = new StreamGeometry();
            var helixGeom2 = new StreamGeometry();
            int steps = 36;
            using (var ctx1 = helixGeom1.Open())
            using (var ctx2 = helixGeom2.Open())
            {
                bool first = true;
                for (int s = 0; s <= steps; s++)
                {
                    double t = (double)s / steps;
                    var pos = Cubic(p0, p1, p2, p3, t);
                    var tan = CubicTangent(p0, p1, p2, p3, t);
                    var n = new Vector(-tan.Y, tan.X);

                    double wave = Math.Sin(t * Math.PI * 4 - _time * 4.2);
                    double amp = (8 + _energy * 10) * Math.Sin(t * Math.PI);
                    Point w1 = pos + n * (wave * amp);
                    Point w2 = pos - n * (wave * amp);

                    if (first)
                    {
                        ctx1.BeginFigure(w1, false, false);
                        ctx2.BeginFigure(w2, false, false);
                        first = false;
                    }
                    else
                    {
                        ctx1.LineTo(w1, true, false);
                        ctx2.LineTo(w2, true, false);
                    }
                }
            }
            helixGeom1.Freeze();
            helixGeom2.Freeze();

            var helixPen = FrameBrushCache.Pen4(accColor, _energy * 90 / 255.0, 1.2);
            dc.DrawGeometry(null, helixPen, helixGeom1);
            dc.DrawGeometry(null, helixPen, helixGeom2);
        }

        // 6. ЧАСТИЦЫ ДАННЫХ СО СВЕТЯЩИМИСЯ КОМЕТАМИ (Comet Data Streams)
        if (_particles.Count > 0)
        {
            var gg = new GeometryGroup();
            var tailPen = FrameBrushCache.Pen4(accColor, (70 + _energy * 130) / 255.0, 1.8, PenLineCap.Round);

            foreach (var p in _particles)
            {
                var pos = Cubic(p0, p1, p2, p3, p.T);
                var tan = CubicTangent(p0, p1, p2, p3, p.T);
                var n = new Vector(-tan.Y, tan.X);
                pos += n * (p.Lane * Math.Sin(p.T * Math.PI) * 14);

                if (_energy > 0.08)
                {
                    double tailLen = (7 + _energy * 16) * (0.6 + p.Jitter * 0.4);
                    Point tailPos = pos - tan * tailLen;
                    dc.DrawLine(tailPen, tailPos, pos);
                }

                // До 130 EllipseGeometry на кадр. Само по себе это дешёвый
                // объект, но GeometryGroup пересобирает каждый кадр целиком.
                gg.Children.Add(new EllipseGeometry(pos, p.Size, p.Size));
            }

            var pb = FrameBrushCache.Solid(accColor, 0.55 + _energy * 0.45);
            dc.DrawGeometry(pb, null, gg);
        }

        // 7. PREPARING: СКАНИРУЮЩИЙ ЛУЧ
        if (State == TransferState.Preparing)
        {
            double sx = ((_time * 130) % (w + 120)) - 60;
            // Градиент не зависит от позиции луча, поэтому кэшируется.
            if (_scanGrad is null || _scanGradColor != accColor)
            {
                _scanGrad = new LinearGradientBrush(
                    Color.FromArgb(0, accColor.R, accColor.G, accColor.B),
                    Color.FromArgb(65, accColor.R, accColor.G, accColor.B),
                    new Point(0, 0), new Point(1, 0));
                if (_scanGrad.CanFreeze) _scanGrad.Freeze();
                _scanGradColor = accColor;
            }
            dc.DrawRectangle(_scanGrad, null, new Rect(sx - 40, 8, 80, h - 16));
        }

        // 8. ГОЛОГРАФИЧЕСКИЕ КИБЕР-УЗЛЫ SRC И DST (Cyber Hologram Nodes)
        DrawSciFiNode(dc, p0, SourceLabel, active: _energy > 0.03, glow: _energy, warn: false, isSource: true, accColor);
        DrawSciFiNode(dc, p3, DestLabel, active: done || _energy > 0.03, glow: done ? 1 : _energy, warn: _errorGlow > 0, isSource: false, accColor);

        // 9. ЦЕНТРАЛЬНАЯ АСТРОНАВИГАЦИОННАЯ СФЕРА (Central HUD Reticle)
        double ringR = Math.Min(54, h / 2 - 20);

        // Внешний лимб с делениями компаса
        var reticlePen = FrameBrushCache.Pen4(accColor, 0x35 / 255.0, 1.2);
        dc.DrawEllipse(null, reticlePen, center, ringR + 10, ringR + 10);
        for (int a = 0; a < 360; a += 30)
        {
            Point pOuter = PointOnCircle(center, ringR + 13, a + _time * 4);
            Point pInner = PointOnCircle(center, ringR + 8, a + _time * 4);
            dc.DrawLine(reticlePen, pInner, pOuter);
        }

        // Базовое кольцо прогресса
        dc.DrawEllipse(null, _ringPen, center, ringR, ringR);

        // Дыхание в покое
        double breathe = 0.12 + 0.06 * Math.Sin(_time * 2.2);
        var haloPen = FrameBrushCache.Pen4(accColor, done ? 0.4 : breathe, 13);
        dc.DrawEllipse(null, haloPen, center, ringR, ringR);

        // Двойная неоновая дуга прогресса
        double sweep = Motion.Clamp01(Progress / 100.0) * 360;
        if (sweep > 0.5)
        {
            var arc = new PathGeometry();
            var fig = new PathFigure
            {
                StartPoint = PointOnCircle(center, ringR, -90),
                IsClosed = false
            };
            Point endP = PointOnCircle(center, ringR, -90 + sweep);
            fig.Segments.Add(new ArcSegment(endP, new Size(ringR, ringR),
                0, sweep > 180, SweepDirection.Clockwise, true));
            arc.Figures.Add(fig);
            arc.Freeze();

            var outerArcPen = FrameBrushCache.Pen4(accColor, 90 / 255.0, 12, PenLineCap.Round);
            dc.DrawGeometry(null, outerArcPen, arc);

            dc.DrawGeometry(null, _flowPen7, arc);

            if (sweep < 358)
            {
                dc.DrawEllipse(FrameBrushCache.Solid(accColor, 130 / 255.0), null, endP, 8, 8);
                dc.DrawEllipse(Brushes.White, null, endP, 4, 4);
            }
        }

        // 10. КВАНТОВЫЙ ИМПУЛЬС ЗАВЕРШЕНИЯ (Quantum Pulse)
        if (_pulseT >= 0)
        {
            double t = Motion.Clamp01(_pulseT);
            double e = Motion.EaseOutCubic(t);
            var pen = FrameBrushCache.Pen4(accColor, (1 - e) * (200 / 255.0), 3.5);
            dc.DrawEllipse(null, pen, center, ringR + e * 80, ringR + e * 80);
        }

        // 11. ФОТОННЫЕ ВСПЫШКИ (Burst Photons)
        // До 70 вспышек: раньше на каждое создавалась и замораживалась своя кисть.
        foreach (var b in _bursts)
        {
            var brush = FrameBrushCache.Solid(accColor, b.Life);
            double rr = 3.0 * b.Life + 0.6;
            dc.DrawEllipse(brush, null, b.P, rr, rr);
        }
    }

    private void DrawSciFiNode(DrawingContext dc, Point c, string label, bool active, double glow, bool warn, bool isSource, Color accColor)
    {
        var ab = (SolidColorBrush)(warn ? _warning : _accent);
        Color col = ab.Color;

        if (glow > 0.01)
        {
            dc.DrawEllipse(FrameBrushCache.Solid(col, glow * (55 / 255.0)), null, c, 40, 40);
        }

        // Вращающееся внешнее голографическое кольцо
        double rot = _time * (isSource ? 1.5 : -1.5);
        var ringPen = FrameBrushCache.Pen4(col, 0x40 / 255.0, 1.5);
        dc.DrawEllipse(null, ringPen, c, 28, 28);

        // Радиальные засечки
        for (int a = 0; a < 360; a += 90)
        {
            Point p1 = PointOnCircle(c, 28, a + rot * 40);
            Point p2 = PointOnCircle(c, 32, a + rot * 40);
            dc.DrawLine(ringPen, p1, p2);
        }

        // Основное тело узла
        dc.DrawEllipse(_card, FrameBrushCache.Pen4(col, 0x55 / 255.0, 2), c, 24, 24);
        dc.DrawEllipse(null, _nodeRingPen, c, 15, 15);

        // Активный квантовый ротор
        dc.DrawEllipse(active ? ab : _track, null, c, 6, 6);

        // FormattedText — самая дорогая операция в WPF-рендере. Раньше здесь
        // создавались четыре объекта на кадр (два узла × подпись и тег) плюс
        // столько же Typeface и FontFamily. Теперь всё берётся из кэша.
        double dip = _cachedDpi;
        if (dip <= 0) { dip = _cachedDpi = VisualTreeHelper.GetDpi(this).PixelsPerDip; }

        string tag = isSource ? "[SRC]" : "[DST]";
        var tagFt = FrameTextCache.Tag(tag, active ? ab : _secondary, dip);
        dc.DrawText(tagFt, new Point(c.X - tagFt.Width / 2, c.Y - 40));

        var ft = FrameTextCache.Label(label ?? "", _secondary, dip);
        dc.DrawText(ft, new Point(c.X - ft.Width / 2, c.Y + 34));
    }

    private double _cachedDpi;
    private LinearGradientBrush? _scanGrad;
    private Color _scanGradColor;

    private static Point PointOnCircle(Point c, double r, double deg)
    {
        double a = deg * Math.PI / 180;
        return new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
    }
}
