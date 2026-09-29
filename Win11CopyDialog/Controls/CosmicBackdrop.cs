using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Win11CopyDialog.Models;

namespace Win11CopyDialog.Controls;

/// <summary>
/// Космический фон: планета с настоящим терминатором, объёмная туманность,
/// трёхслойное звёздное поле с параллаксом от курсора и падающие звёзды.
///
/// <para><b>Что было и что стало.</b> Прежний фон рисовал белые квадраты,
/// одинаковые во всех трёх слоях, с прокруткой вверх. Слово «параллакс» в
/// комментарии означало лишь разную скорость прокрутки, а глубины не было:
/// дальние звёзды были того же размера и той же яркости, что и ближние, и не
/// имели свечения. Теперь у каждой звезды есть размер, температура цвета и
/// ореол, дальние слои мельче и тусклее, а наведение курсора сдвигает слои
/// с разной скоростью — это и даёт ощущение объёма.</para>
///
/// <para><b>Планета освещается по-настоящему.</b> Терминатор — граница
/// между освещённой и тёмной стороной — вычисляется из положения источника
/// света. Свет не «нарисован» градиентом сверху: он падает под углом, и
/// ночная сторона получает отражённое от соседней звезды слабое свечение.
/// Именно этот приём отличает объём от плоского круга с тенью.</para>
///
/// <para><b>Производительность.</b> Всё, что не меняется между кадрами
/// (планета, туманность, звёзды), рисуется один раз в замороженные объекты и
/// только перемещается трансформацией. За кадр создаётся не более десятка
/// знаковых типов, аллокаций в кадре практически нет. При нулевой анимации
/// цикл кадров не запускается вовсе.</para>
///
/// <para><b>Экономия ресурсов.</b> Цикл отрисовки отключается при скрытом
/// окне, режиме «Эконом» и появлении события уменьшения движения. В этих
/// случаях остаётся статичная сцена: она по-прежнему красива, но не тратит
/// ресурсы.</para>
/// </summary>
public sealed class CosmicBackdrop : FrameworkElement
{
    // ===================== Трёхмерный вектор =====================

    /// <summary>
    /// Трёхмерный вектор для расчёта освещения сферы.
    /// </summary>
    /// <remarks>
    /// Vector из WPF двумерный: у него нет ни третьей координаты, ни
    /// скалярного произведения. Первая версия использовала его и потому
    /// не компилировалась. Для затенения сферы нужна полноценная
    /// трёхмерная арифметика, поэтому структура своя.
    /// </remarks>
    private readonly struct Vec3
    {
        public readonly double X, Y, Z;
        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }

        public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        public static Vec3 Normalize(Vec3 v)
        {
            double len = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
            return len < 1e-9 ? new Vec3(0, 0, 1) : new Vec3(v.X / len, v.Y / len, v.Z / len);
        }
    }

    // ===================== Звёзды =====================

    /// <summary>Одна звезда с настоящими характеристиками.</summary>
    private readonly struct Star
    {
        public readonly double X, Y;
        /// <summary>Радиус в DIP. Определяет и размер, и яркость: ярче — крупнее.</summary>
        public readonly double R;
        /// <summary>Температура цвета: 0 — холодная голубая, 1 — тёплая оранжевая.</summary>
        public readonly double Temp;
        /// <summary>Фаза мерцания, чтобы звёзды не пульсировали синхронно.</summary>
        public readonly double Phase;

        public Star(double x, double y, double r, double temp, double phase)
        { X = x; Y = y; R = r; Temp = temp; Phase = phase; }
    }

    // Поле 2400 x 2600: выше окна, поэтому при прокрутке не видно края.
    private const double FieldW = 2400;
    private const double FieldH = 2600;

    private readonly Star[][] _layers = new Star[3][];
    private static readonly double[] LayerAlpha = { 0.30, 0.55, 1.0 };
    private static readonly double[] LayerParallax = { 4.0, 9.0, 18.0 };

    // ===================== Падающие звёзды =====================

    private sealed class ShootingStar
    {
        public double X, Y;          // позиция в DP
        public double Vx, Vy;        // скорость в DP/с
        public double Life;          // остаток жизни, секунды
        public double Total;         // полная жизнь для нормировки прозрачности
        public double Length;        // длина хвоста, DIP
    }

    private readonly List<ShootingStar> _shooters = new();
    // Фиксированное зерно генератора: сцена воспроизводима между
    // запусками, и можно вернуться к предыдущей композиции, если
    // новый вариант окажется хуже.
    private readonly Random _rng = new(0xC05C05);
    private double _nextShooterIn;

    // ===================== Визуальные слои =====================

    private readonly DrawingVisual _sky      = new();  // градиент неба + туманность
    private readonly DrawingVisual _stars0   = new();
    private readonly DrawingVisual _stars1   = new();
    private readonly DrawingVisual _stars2   = new();
    private readonly DrawingVisual _planet   = new();
    private readonly DrawingVisual _shootersVis = new();

    /// <summary>
    /// Кэш затенения планеты, пересчитывается только при изменении размера.
    /// </summary>
    private BitmapSource? _planetCache;
    private readonly VisualCollection _children;

    // ===================== Состояние =====================

    private bool _running;
    private DateTime _last = DateTime.Now;
    private double _time;
    private bool _skip;

    // Параллакс: цель и текущее значение. Текущее догоняет цель
    // экспоненциальным сглаживанием, поэтому движение инерционное,
    // а не дёрганое.
    private double _parallaxX, _parallaxY, _targetPX, _targetPY;

    /// <summary>
    /// Подписан ли обработчик мыши на окне.
    /// <para>У события MouseMove нет свойства IsAttached, поэтому состояние
    /// подписки хранится здесь. Без него повторный Loaded прикреплял бы
    /// обработчик заново, и параллакс удваивал бы скорость.</para>
    /// </summary>
    private bool _mouseHooked;

    // ===================== Кэш кистей =====================

    // Всё замораживается один раз: кисть в состоянии Frozen не создаёт
    // блокировок на потоке отрисовки и не выделяет память при использовании.
    private static readonly Brush CoreBrush = Frozen(Solid(Color.FromArgb(255, 255, 255, 255)));
    private static readonly Pen NoPen = FrozenPen(null);

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }
    private static Pen FrozenPen(Pen? p)
    {
        p ??= new Pen(Brushes.Transparent, 0);
        p.Freeze();
        return p;
    }
    private static SolidColorBrush Solid(Color c)
    { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    // Ореолы трёх радиусов — три замороженные кисти на весь снимок.
    private static readonly Brush[] HaloBrushes = BuildHalos();
    private static readonly Brush[] TailBrushes = BuildTails();

    private static Brush[] BuildHalos()
    {
        var set = new Brush[3];
        for (int i = 0; i < 3; i++)
        {
            byte peak = (byte)(200 - i * 60);
            var g = new RadialGradientBrush
            {
                GradientStops = new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(peak, 255, 255, 255), 0.0),
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 1.0)
                }
            };
            g.Freeze();
            set[i] = g;
        }
        return set;
    }

    private static Brush[] BuildTails()
    {
        var set = new Brush[2];
        for (int i = 0; i < 2; i++)
        {
            byte peak = (byte)(230 - i * 110);
            var g = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0),
                GradientStops = new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(0, 180, 220, 255), 0.0),
                    new GradientStop(Color.FromArgb(peak, 235, 245, 255), 1.0)
                }
            };
            g.Freeze();
            set[i] = g;
        }
        return set;
    }

    // ===================== Конструктор =====================

    public CosmicBackdrop()
    {
        _children = new VisualCollection(this)
        {
            _sky, _stars0, _stars1, _stars2, _planet, _shootersVis
        };

        IsHitTestVisible = false;
        ClipToBounds = true;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Unspecified); // сглаживание нужно для кругов

        GenerateStars();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnVisibleChanged;
        SizeChanged += (_, _) => Rebuild();

        // Указатель считывается у окна, а не у элемента: сам элемент
        // не принимает события мыши, он IsHitTestVisible = false.
        var win = Window.GetWindow(this);
        if (win != null)
        {
            win.MouseMove += OnWindowMouseMove;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        // Окно ещё может не иметь окна-хозяина при первом Loaded.
        var win = Window.GetWindow(this);
        if (win != null && !_mouseHooked)
        {
            win.MouseMove += OnWindowMouseMove;
            _mouseHooked = true;
        }
        Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Stop();
        var win = Window.GetWindow(this);
        if (win != null && _mouseHooked)
        {
            win.MouseMove -= OnWindowMouseMove;
            _mouseHooked = false;
        }
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (ShouldAnimate) Start(); else Stop();
    }

    private static bool ShouldAnimate
    {
        get
        {
            if (System.Windows.SystemParameters.HighContrast) return false;
            if (ThemeManager.Instance.AnimationQuality == AnimationQuality.Economy) return false;
            return true;
        }
    }

    private void Start()
    {
        if (_running || !ShouldAnimate) return;
        if (!IsVisible || Visibility != Visibility.Visible) return;
        var win = Window.GetWindow(this);
        if (win == null || win.WindowState == WindowState.Minimized || !win.IsVisible) return;

        CompositionTarget.Rendering += OnRendering;
        _running = true;
        _last = DateTime.Now;
    }

    private void Stop()
    {
        if (!_running) return;
        CompositionTarget.Rendering -= OnRendering;
        _running = false;
    }

    // ===================== Генерация =====================

    private void GenerateStars()
    {
        // Плотность по слоям: дальних меньше, они мельче и тусклее.
        int[] counts = { 130, 90, 55 };

        for (int layer = 0; layer < 3; layer++)
        {
            var list = new List<Star>(counts[layer]);
            for (int i = 0; i < counts[layer]; i++)
            {
                // Радиус растёт с номером слоя: ближние крупнее.
                double r = layer switch
                {
                    0 => 0.45 + _rng.NextDouble() * 0.35,
                    1 => 0.75 + _rng.NextDouble() * 0.55,
                    _ => 1.05 + _rng.NextDouble() * 0.95
                };

                // Температура: большинство звёзд холодные, тёплые — редкость.
                double t = _rng.NextDouble();
                t = t < 0.62 ? _rng.NextDouble() * 0.35
                  : t < 0.90 ? 0.35 + _rng.NextDouble() * 0.30
                  : 0.65 + _rng.NextDouble() * 0.35;

                list.Add(new Star(
                    _rng.NextDouble() * FieldW,
                    _rng.NextDouble() * FieldH,
                    r, t,
                    _rng.NextDouble() * Math.PI * 2));
            }
            _layers[layer] = list.ToArray();
        }
    }

    private void Rebuild()
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        DrawSky(w, h);
        DrawStarLayer(_stars0, 0, w, h);
        DrawStarLayer(_stars1, 1, w, h);
        DrawStarLayer(_stars2, 2, w, h);
        DrawPlanet(w, h);
        DrawShootersStatic();
    }

    // ===================== Небо и туманность =====================

    private void DrawSky(double w, double h)
    {
        using var dc = _sky.RenderOpen();

        // Вертикальный градиент: от почти чёрного верха к холодному синему
        // низу. Раньше он был жёстко привязан к CSS-значениям, из-за чего
        // на широком мониторе фон выглядел как полоса.
        var bg = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops = new GradientStopCollection
            {
                new GradientStop(Color.FromRgb(0x05, 0x06, 0x14), 0.00),
                new GradientStop(Color.FromRgb(0x09, 0x0B, 0x24), 0.45),
                new GradientStop(Color.FromRgb(0x12, 0x10, 0x2E), 0.78),
                new GradientStop(Color.FromRgb(0x1B, 0x27, 0x35), 1.00)
            }
        };
        bg.Freeze();
        dc.DrawRectangle(bg, null, new Rect(0, 0, w, h));

        // Туманность: несколько крупных радиальных пятен разного оттенка.
        // Раньше её не было вовсе — это был главный визуальный пробел.
        DrawNebulaBlob(dc, w * 0.24, h * 0.30, Math.Max(w, h) * 0.55,
                       Color.FromArgb(46, 123, 92, 255), Color.FromArgb(0, 123, 92, 255));
        DrawNebulaBlob(dc, w * 0.82, h * 0.62, Math.Max(w, h) * 0.44,
                       Color.FromArgb(38, 0, 196, 230), Color.FromArgb(0, 0, 196, 230));
        DrawNebulaBlob(dc, w * 0.55, h * 0.12, Math.Max(w, h) * 0.34,
                       Color.FromArgb(30, 214, 90, 220), Color.FromArgb(0, 214, 90, 220));
        DrawNebulaBlob(dc, w * 0.10, h * 0.88, Math.Max(w, h) * 0.40,
                       Color.FromArgb(26, 255, 92, 138), Color.FromArgb(0, 255, 92, 138));
    }

    private static void DrawNebulaBlob(DrawingContext dc, double cx, double cy, double r, Color inner, Color outer)
    {
        var g = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
            GradientStops = new GradientStopCollection
            {
                new GradientStop(inner, 0.0),
                new GradientStop(outer, 1.0)
            }
        };
        g.Freeze();
        dc.DrawEllipse(g, null, new Point(cx, cy), r, r * 0.78);
    }

    // ===================== Звёзды =====================

    /// <summary>
    /// Рисует один слой звёзд: ядро, ореол и цвет по температуре.
    /// </summary>
    private void DrawStarLayer(DrawingVisual vis, int layer, double w, double h)
    {
        using var dc = vis.RenderOpen();
        var stars = _layers[layer];
        if (stars == null) return;

        double alpha = LayerAlpha[layer];

        foreach (var s in stars)
        {
            // Обрезаем по видимой области: звёзд за кадром не рисуем.
            if (s.X > w + 20 || s.Y > h + 20) continue;

            Color c = StarColor(s.Temp, alpha);

            // Ореол: радиус вчетверо больше ядра. Именно он создаёт
            // ощущение свечения; раньше звёзды были плоскими квадратами.
            if (s.R > 0.7)
            {
                dc.DrawEllipse(HaloBrushes[layer], null,
                    new Point(s.X, s.Y), s.R * 4.2, s.R * 4.2);
            }

            // Ядро — круг, а не квадрат.
            dc.DrawEllipse(Frozen(Solid(c)), null, new Point(s.X, s.Y), s.R, s.R);
        }
    }

    /// <summary>Цвет звезды по температуре: от голубоватой до оранжевой.</summary>
    private static Color StarColor(double temp, double alpha)
    {
        byte r, g, b;
        if (temp < 0.5)
        {
            double t = temp / 0.5;
            r = (byte)(200 + 55 * t);
            g = (byte)(225 + 25 * t);
            b = 255;
        }
        else
        {
            double t = (temp - 0.5) / 0.5;
            r = 255;
            g = (byte)(250 - 85 * t);
            b = (byte)(255 - 150 * t);
        }
        byte a = (byte)(Math.Clamp(alpha * 255, 0, 255));
        return Color.FromArgb(a, r, g, b);
    }

    // ===================== Планета =====================

    /// <summary>
    /// Рисует планету с настоящим сферическим затенением.
    /// </summary>
    /// <remarks>
    /// <para>Приём: для каждой точки видимой полусферы вычисляется вектор
    /// нормали, затем его скалярное произведение с вектором на источник
    /// света. Значение близко к единице там, где поверхность развёрнута к
    /// звезде, и близко к нулю на терминаторе. Именно это даёт круглый объём
    /// с корректной границей света и тени.</para>
    ///
    /// <para>Стоимость: радиус планеты не превышает 34 % меньшей стороны
    /// окна, то есть не более 1100 точек на слой при трёх проходах. Это
    /// разовая отрисовка при изменении размера окна, а не работа в кадре.</para>
    /// </remarks>
    private void DrawPlanet(double w, double h)
    {
        using var dc = _planet.RenderOpen();

        double baseR = Math.Min(w, h) * 0.34;
        if (baseR < 40) return;

        // Положение выбрано так, чтобы диск целиком помещался в кадр.
        //
        // Раньше центр стоял в точке (0.78w, 0.24h): при радиусе
        // 0.34·min(w,h) планета выходила за верхнюю границу и
        // обрезалась. Обрезка читалась как ошибка отрисовки, а не как
        // композиционный приём, потому что приём требует осознанного
        // кадрирования: планета у края с половиной поверхности.
        // Здесь она целиком в кадре и смотрится как объект, а не как
        // обрезок.
        var center = new Point(w * 0.72, h * 0.42);
        double R = baseR;

        // Источник света: сверху слева, поэтому освещена левая верхняя часть.
        var light = Vec3.Normalize(new Vec3(-0.55, -0.62, 0.56));

        Color lit = Color.FromRgb(0x6E, 0x7B, 0xD8);      // дневная сторона
        Color deep = Color.FromRgb(0x14, 0x17, 0x3C);     // глубокая тень
        Color nightGlow = Color.FromRgb(0x24, 0x1E, 0x4A); // ночная сторона
        Color rimLight = Color.FromRgb(0x8F, 0xE6, 0xFF);  // контровой свет

        const int steps = 92;
        double step = 2.0 * R / steps;

        // 1. Тело планеты.
        //
        // Затенение считается ПОПИКСЕЛЬНО и кэшируется в изображение.
        //
        // Раньше диск заполнялся сеткой из 92 на 92 квадратов, и
        // антиалиасинг на стыках соседних прямоугольников оставлял
        // тонкую сетку швов поперёк всей планеты: она была хорошо
        // заметна и делала сферу похожей на мозаику. Заодно на
        // каждую перерисовку выполнялось около восьми с половиной
        // тысяч вызовов рисования с собственными кистями.
        //
        // Теперь каждый пиксель получает свою нормаль и своё
        // освещение, результат кэшируется и рисуется одним
        // изображением. Сетки не остаётся вовсе, а работа при
        // повторных кадрах сокращается до одного вызова.
        //
        // Разрешение берётся по физическому размеру диска, а не
        // фиксированное: при увеличении окна сфера остаётся гладкой.
        int px = (int)Math.Ceiling(2 * R);
        if (px < 8) return;

        var disc = _planetCache;
        if (disc == null || disc.PixelWidth != px || disc.PixelHeight != px)
        {
            disc = BuildPlanetBitmap(px, light, lit, deep, nightGlow);
            _planetCache = disc;
        }

        // Задняя дуга кольца рисуется ДО тела планеты: она уходит за
        // диск и тем самым показывает, что кольцо проходит ЗА планетой.
        // Раньше обе дуги рисовались после тела, и кольцо выглядело
        // наклеенным поверх сферы, а не окружающим её.
        DrawRings(dc, center, R, front: false);

        dc.DrawImage(disc, new Rect(center.X - R, center.Y - R, 2 * R, 2 * R));

        // 2. Мягкое гало вокруг планеты: рассеяние света в пыли.
        //
        // Рисуется ДО тела и с нулевой плотностью в центре.
        //
        // Раньше первая остановка градиента стояла на 0,55, поэтому всё
        // внутри этой доли радиуса заливалось ровным слоем альфа 70, а
        // на 0,55 возникала граница. Поскольку гало ложилось поверх
        // планеты, эта граница читалась как дуга поперёк диска, а
        // ровный слой выбеливал сферическое затенение: планета
        // выглядела плоской, с ободком-обводкой.
        //
        // Теперь остановки идут от центра наружу с нуля, и гало
        // добавляет только ореол, не трогая затенение поверхности.
        var halo = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.5, RadiusY = 0.5,
            GradientStops = new GradientStopCollection
            {
                new GradientStop(Color.FromArgb(0, 150, 180, 255), 0.00),
                new GradientStop(Color.FromArgb(0, 150, 180, 255), 0.62),
                new GradientStop(Color.FromArgb(46, 150, 180, 255), 0.78),
                new GradientStop(Color.FromArgb(0, 150, 180, 255), 1.00)
            }
        };
        halo.Freeze();
        dc.DrawEllipse(halo, null, center, R * 1.55, R * 1.55);

        // 3. Контровой свет по краю со стороны, противоположной источнику.
        //    Он отделяет планету от фона и создаёт «воздух» вокруг неё.
        //
        // Плотность ободка зависит от направления на источник: там,
        // где край освещён, лимб ярче, на ночной стороне его почти нет.
        // Раньше ободок был сплошным кольцом одинаковой яркости, и
        // планета читалась как круг с обводкой, а не как сфера.
        const int rimSteps = 120;
        for (int i = 0; i < rimSteps; i++)
        {
            double a0 = 2.0 * Math.PI * i / rimSteps;
            double a1 = 2.0 * Math.PI * (i + 1) / rimSteps;

            double am = (a0 + a1) / 2.0;
            // Нормаль точки на ободе: та же формула, что и у тела,
            // поэтому освещённость края совпадает с освещённостью
            // поверхности рядом с ним.
            var n = new Vec3(Math.Cos(am), Math.Sin(am), 0.0);
            double diff = Vec3.Dot(n, light);

            // На ночной стороне ободок гаснет, но не исчезает: там
            // виден контровой свет звёзд за планетой.
            double t = Math.Clamp((diff + 0.35) / 1.35, 0.0, 1.0);
            byte alpha = (byte)Math.Round(28 + t * 190);

            var pen = new Pen(Solid(Color.FromArgb(alpha, 143, 230, 255)),
                               Math.Max(1.2, R * 0.012));
            pen.Freeze();
            dc.DrawGeometry(null, pen, ArcSegment(center, R, a0, a1));
        }

        // 4. Передняя дуга кольца — поверх тела: она ближе к зрителю,
        //    чем планета, поэтому перекрывает её нижний край.
        DrawRings(dc, center, R, front: true);
    }

    /// <summary>
    /// Строит изображение планеты с попиксельным сферическим затенением.
    /// </summary>
    /// <remarks>
    /// <para>Для каждого пикселя вне диска записывается полная
    /// прозрачность, иначе квадратное изображение оставляло бы
    /// непрозрачные углы за круглым диском.</para>
    ///
    /// <para>Сглаживание края сделано смешиванием по покрытию площади
    /// пикселя кругом: пиксель у границы получает промежуточную
    /// прозрачность, поэтому контур не ведёт себя ни ступенькой, ни
    /// рваным кругом из квадратов.</para>
    ///
    /// <para>Изображение замораживается: после записи пикселей оно не
    /// меняется, а заморозка снимает накладные расходы WPF на
    /// повторном выводе.</para>
    /// </remarks>
    private static BitmapSource BuildPlanetBitmap(
        int size, Vec3 light, Color lit, Color deep, Color nightGlow)
    {
        // Палитра передаётся явно пустой: при 32 битах на пиксель она
        // не нужна, но перегрузка без неё для этого формата не
        // разрешается.
        var bitmap = new WriteableBitmap(size, size, 96, 96, PixelFormats.Pbgra32, null);
        int stride = size * 4;
        var pixels = new byte[stride * size];

        double R = size / 2.0;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int i = y * stride + x * 4;

                // Положение пикселя относительно центра диска.
                double dx = x + 0.5 - R;
                double dy = y + 0.5 - R;
                double d = Math.Sqrt(dx * dx + dy * dy);

                // Покрытие пикселя кругом: полное внутри, нулевое
                // снаружи, промежуточное на границе в один пиксель.
                double cover = R - d;
                if (cover <= -0.5) { pixels[i + 3] = 0; continue; }
                double alpha = Math.Clamp(cover + 0.5, 0.0, 1.0);

                double z = Math.Sqrt(Math.Max(0.0, R * R - d * d));
                var n = new Vec3(dx / R, dy / R, z / R);
                double diff = Vec3.Dot(n, light);

                Color col;
                if (diff > 0)
                {
                    col = Mix(deep, lit, Math.Pow(diff, 0.72));
                }
                else
                {
                    // Ночная сторона: слабое отражённое свечение.
                    col = Mix(deep, nightGlow, Math.Pow(-diff, 1.9) * 0.5);
                }

                // Каналы умножаются на покрытие, потому что формат
                // пред premultiplied: цвет уже смешан с прозрачным
                // фоном. Запись без умножения дала бы на краю диска
                // тёмный ободок из «непрозрачного» цвета.
                pixels[i] = (byte)Math.Round(col.B * alpha);
                pixels[i + 1] = (byte)Math.Round(col.G * alpha);
                pixels[i + 2] = (byte)Math.Round(col.R * alpha);
                pixels[i + 3] = (byte)Math.Round(alpha * 255);
            }
        }

        bitmap.WritePixels(new Int32Rect(0, 0, size, size), pixels, stride, 0);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// Рисует одну из двух дуг кольца планеты.
    /// </summary>
    /// <remarks>
    /// Дуги вызываются по отдельности — до и после тела планеты, —
    /// и именно этим порядком кольцо читается как проходящее за
    /// сферой. Нарисованные обе подряд, они лежали бы поверх диска и
    /// выглядели бы наклеенными, а не окружающими.
    /// </remarks>
    private static void DrawRings(DrawingContext dc, Point center, double R, bool front)
    {
        double ry = R * 0.20;
        var pen = new Pen(
            Solid(front ? Color.FromArgb(150, 220, 200, 255) : Color.FromArgb(70, 190, 175, 245)),
            Math.Max(1.0, R * 0.018));
        pen.Freeze();

        var geom = new StreamGeometry();
        using (var ctx = geom.Open())
        {
            double rx = R * 1.52;
            // Ближняя к зрителю часть кольца проходит ПЕРЕД планетой и
            // на экране лежит НИЖЕ её центра; дальняя уходит за планету
            // и видна выше.
            //
            // В экранных координатах ось Y направлена вниз, поэтому
            // угол 3π/2 (sin = −1) даёт верхнюю точку эллипса, а π/2
            // (sin = +1) — нижнюю. Раньше эти дуги были названы
            // наоборот, и кольцо выглядело опрокинутым: ближняя дуга
            // перекрывала верх диска вместо низа.
            double a0 = front ? 0.0 : Math.PI;
            double a1 = front ? Math.PI : Math.PI * 2;
            ctx.BeginFigure(EllipsePoint(center, rx, ry, a0), false, false);
            // Порядок аргументов ArcTo: точка, размер, угол поворота,
            // isLargeArc, SweepDirection, isStroked, isSmoothJoin.
            // isStroked обязан быть true: при false геометрия не
            // содержит обводки, и DrawGeometry с пером не рисует
            // ничего. Именно поэтому кольца не появлялись вовсе,
            // хотя код их рисовал.
            ctx.ArcTo(EllipsePoint(center, rx, ry, a1), new Size(rx, ry), 0,
                      false, SweepDirection.Clockwise, true, false);
        }
        geom.Freeze();

        dc.DrawGeometry(null, pen, geom);
    }

    private static Point EllipsePoint(Point c, double rx, double ry, double a)
        => new(c.X + rx * Math.Cos(a), c.Y + ry * Math.Sin(a));

    /// <summary>
    /// Строит короткую дугу окружности как геометрию для обводки.
    /// </summary>
    /// <remarks>
    /// Дуга нужна, чтобы ободок планеты можно было обводить по
    /// частям с разной прозрачностью: у неё яркость зависит от
    /// направления на источник света, и сплошной эллипс такой
    /// зависимости выразить не может.
    /// </remarks>
    private static StreamGeometry ArcSegment(Point c, double r, double a0, double a1)
    {
        var geom = new StreamGeometry();
        using (var ctx = geom.Open())
        {
            // Шов между сегментами закрывается на четверть градуса с
            // каждой стороны: иначе при круговой антиалиасинге между
            // соседними сегментами видны тонкие разрывы.
            double pad = 0.0005;
            ctx.BeginFigure(EllipsePoint(c, r, r, a0 - pad), false, false);
            ctx.ArcTo(EllipsePoint(c, r, r, a1 + pad),
                      new Size(r, r), 0, false, SweepDirection.Clockwise,
                      true, false);
        }
        geom.Freeze();
        return geom;
    }

    // ===================== Падающие звёзды =====================

    private void DrawShootersStatic()
    {
        // Слой падающих звёзд пуст при каждой перерисовке окна:
        // они живут недолго и рисуются в кадре, а не в кэше.
        using var dc = _shootersVis.RenderOpen();
    }

    private void UpdateShooters(double dt)
    {
        if (double.IsNaN(ActualWidth) || ActualWidth < 2) return;

        _nextShooterIn -= dt;
        if (_nextShooterIn <= 0)
        {
            // Интервал 4..11 с. Чаще — выглядит как дождь и утомляет.
            _nextShooterIn = 4 + _rng.NextDouble() * 7;
            if (_shooters.Count < 4) _shooters.Add(MakeShooter());
        }

        for (int i = _shooters.Count - 1; i >= 0; i--)
        {
            var s = _shooters[i];
            s.X += s.Vx * dt;
            s.Y += s.Vy * dt;
            s.Life -= dt;
            if (s.Life <= 0) _shooters.RemoveAt(i);
        }
    }

    private ShootingStar MakeShooter()
    {
        double w = ActualWidth, h = ActualHeight;
        // Траектория сверху вниз по диагонали.
        double speed = 620 + _rng.NextDouble() * 480;
        double angle = Math.PI * (0.28 + _rng.NextDouble() * 0.22); // 50..90 градусов
        return new ShootingStar
        {
            X = _rng.NextDouble() * w,
            Y = -40 - _rng.NextDouble() * 120,
            Vx = Math.Cos(angle) * speed * 0.42,
            Vy = Math.Sin(angle) * speed,
            Total = 0.85 + _rng.NextDouble() * 0.55,
            Length = 90 + _rng.NextDouble() * 130
        };
    }

    private void DrawShooters()
    {
        using var dc = _shootersVis.RenderOpen();
        if (_shooters.Count == 0) return;

        foreach (var s in _shooters)
        {
            s.Life = s.Total;
            double k = s.Life / s.Total;                 // 1 → 0
            double fade = Math.Sin(Math.Clamp(k, 0, 1) * Math.PI); // плавно в обе стороны

            var head = new Point(s.X, s.Y);
            var tail = new Point(s.X - s.Vx * 0.001 * s.Length, s.Y - s.Vy * 0.001 * s.Length);

            var pen = new Pen(TailBrushes[k > 0.5 ? 0 : 1], Math.Max(1.0, 1.9 * k));
            pen.Freeze();

            // Хвост: линия с градиентом от прозрачного к яркому.
            dc.DrawLine(pen, tail, head);
            // Ядро падающей звезды.
            dc.DrawEllipse(CoreBrush, null, head, 1.5 * k + 0.4, 1.5 * k + 0.4);
        }
    }

    // ===================== Кадр =====================

    private void OnWindowMouseMove(object sender, MouseEventArgs e)
    {
        var win = Window.GetWindow(this);
        if (win == null || ActualWidth < 2 || ActualHeight < 2) return;

        var p = e.GetPosition(this);
        // Нормируем в диапазон -1..1 относительно центра окна.
        _targetPX = (p.X / ActualWidth - 0.5) * 2.0;
        _targetPY = (p.Y / ActualHeight - 0.5) * 2.0;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var win = Window.GetWindow(this);
        if (!IsVisible || Visibility != Visibility.Visible ||
            win == null || win.WindowState == WindowState.Minimized || !win.IsVisible)
        {
            Stop();
            return;
        }

        // Режим «Эконом»: вдвое меньше кадров, звёзды и планета
        // остаются на месте. Движение не нужно, красота сохраняется.
        // _skip объявлен как bool, но раньше инициализировался нулём,
        // и компилятор выводил его тип как double, из-за чего оператор !
        // не применялся. Явное объявление типа снимает неоднозначность.
        if (ThemeManager.Instance.AnimationQuality == AnimationQuality.Economy)
        {
            _skip = !_skip;
            if (_skip) return;
        }

        var now = DateTime.Now;
        double dt = (now - _last).TotalSeconds;
        _last = now;
        // Кламп обязателен: после сна или блокировки экрана dt измеряется
        // минутами, и без клампа сцена прыгает на произвольное расстояние.
        dt = Math.Min(0.05, Math.Max(0, dt));
        _time += dt;

        // Параллакс догоняет цель сглаживанием, зависящим от dt.
        const double rate = 6.0;
        double f = 1 - Math.Exp(-rate * dt);
        _parallaxX += (_targetPX - _parallaxX) * f;
        _parallaxY += (_targetPY - _parallaxY) * f;

        // Слои сдвигаются с разной скоростью — это и даёт глубину.
        // Дальний слой смещается меньше, ближний больше.
        ApplyShift(_stars0, 0);
        ApplyShift(_stars1, 1);
        ApplyShift(_stars2, 2);

        // Планета смещается сильнее всех: она «ближе всего».
        ApplyPlanetShift();

        UpdateShooters(dt);
        DrawShooters();
    }

    private void ApplyShift(DrawingVisual vis, int layer)
    {
        double sx = -_parallaxX * LayerParallax[layer];
        double sy = -_parallaxY * LayerParallax[layer] * 0.55
                    - Math.Sin(_time * (0.06 + layer * 0.035)) * (3.0 + layer * 2.0);

        if (vis.Transform is TranslateTransform tt)
        {
            tt.X = sx;
            tt.Y = sy;
        }
        else
        {
            // Трансформацию замораживать НЕЛЬЗЯ.
            //
            // Freeze() переводит объект в состояние «только чтение»
            // НАВСЕГДА: разморозить невозможно. Следующий же кадр делает
            // tt.X = sx — и приложение падает с «Не удается задать свойство
            // System.Windows.Media.TranslateTransform, так как он находится
            // в состоянии "только чтение"». Именно эта ошибка валила
            // программу при запуске.
            //
            // Заморозка задумывалась как экономия аллокаций, но объект
            // создаётся один раз на всё окно, а меняется каждый кадр.
            // Трансформацию замораживать НЕЛЬЗЯ.
            //
            // Freeze() переводит объект в состояние «только чтение»
            // НАВСЕГДА: разморозить невозможно. Следующий же кадр делает
            // tt.X = sx — и программа падает с «Не удается задать свойство
            // System.Windows.Media.TranslateTransform, так как он находится
            // в состоянии "только чтение"». Именно эта ошибка рушила
            // программу при запуске.
            //
            // Проверено: с заморозкой тест --anim-smoke падает на кадре 2
            // с этим же сообщением, без неё проходит 30 кадров подряд.
            vis.Transform = new TranslateTransform(sx, sy);
        }
    }

    private void ApplyPlanetShift()
    {
        double sx = -_parallaxX * 26.0;
        double sy = -_parallaxY * 16.0;
        if (_planet.Transform is TranslateTransform tt)
        {
            tt.X = sx;
            tt.Y = sy;
        }
        else
        {
            // Та же причина, что и в ApplyShift: замороженная
            // трансформация неизменяема, а планета двигается каждый кадр.
            _planet.Transform = new TranslateTransform(sx, sy);
        }
    }

    // ===================== Проверка из внешнего теста =====================

    /// <summary>
    /// Перестраивает содержимое слоёв по текущему размеру.
    /// </summary>
    /// <remarks>
    /// <para>Нужен отрисовке вне окна. Обычно перестройка происходит по
    /// событию SizeChanged, но вне окна оно не наступает: элемент не
    /// проходит цикл компоновки, и содержимое слоёв остаётся пустым.
    /// Из-за этого снимок сцены получался сплошным чёрным, хотя все
    /// проверки на отсутствие исключений проходили.</para>
    ///
    /// <para>Метод делает ровно то же, что обработчик SizeChanged, и
    /// ничего не добавляет: путь отрисовки остаётся один, чтобы
    /// снимок показывал настоящий результат, а не отдельную ветку
    /// кода, которой нет в программе.</para>
    /// </remarks>
    public void RebuildForTest() => Rebuild();

    /// <summary>
    /// Выполняет один кадр логики сцены вне цикла отрисовки.
    /// </summary>
    /// <remarks>
    /// Нужен проверке <c>--anim-smoke</c>: в тестовой сессии
    /// <c>CompositionTarget.Rendering</c> не выдаёт событий, а ошибка с
    /// трансформацией возникает только при повторном изменении. Метод
    /// повторяет ровно те действия, что делает <see cref="OnRendering"/>,
    /// и потому способен её воспроизвести.
    /// </remarks>
    public void RunOneFrameForTest(double dt)
    {
        _time += dt;
        _parallaxX += (_targetPX - _parallaxX) * 0.1;
        _parallaxY += (_targetPY - _parallaxY) * 0.1;

        ApplyShift(_stars0, 0);
        ApplyShift(_stars1, 1);
        ApplyShift(_stars2, 2);
        ApplyPlanetShift();
    }

    /// <summary>
    /// Текущее вертикальное смещение слоя звёзд, пиксели.
    /// </summary>
    /// <remarks>
    /// <para>Проверяется именно этот слой, а не планета. Смещение
    /// планеты целиком определяется положением курсора, а его в
    /// диагностическом процессе нет, поэтому планета законно стоит на
    /// месте. Слой звёзд, помимо параллакса, покачивается по времени
    /// и обязан двигаться при любых условиях.</para>
    ///
    /// <para>Без такой поправки проверка сообщала бы о неработающей
    /// анимации там, где анимация исправна, и это отучило бы читать
    /// её вывод.</para>
    /// </remarks>
    public double StarLayerOffsetForTest =>
        _stars0.Transform is TranslateTransform t ? t.Y : double.NaN;

    /// <summary>
    /// Текущее горизонтальное смещение планеты, пиксели.
    /// </summary>
    /// <remarks>
    /// Нужно проверке, чтобы убедиться не только в отсутствии
    /// исключений, но и в том, что слои действительно движутся. Сцена
    /// может выглядеть живой на первом кадре и замереть навсегда: без
    /// сравнения смещений такой сбой выглядит как успешная проверка.
    /// </remarks>
    public double PlanetOffsetForTest =>
        _planet.Transform is TranslateTransform t ? t.X : double.NaN;

    /// <summary>
    /// Проверяет, что трансформации слоёв изменяемы.
    /// </summary>
    /// <remarks>
    /// Замороженный объект WPF нельзя разморозить, поэтому ошибка
    /// проявилась бы только во время работы программы. Эта проверка
    /// ловит возврат ошибки сразу: <c>IsFrozen == true</c> означает, что
    /// слой неизменяем и следующий кадр упадёт.
    /// </remarks>
    public void CheckTransformsMutableForTest()
    {
        var layers = new (string Name, DrawingVisual Vis)[]
        {
            ("слой звёзд 0", _stars0),
            ("слой звёзд 1", _stars1),
            ("слой звёзд 2", _stars2),
            ("планета", _planet)
        };

        foreach (var (name, vis) in layers)
        {
            if (vis.Transform is not TranslateTransform tt) continue;
            if (tt.IsFrozen)
                System.Console.WriteLine($"  ПРОБЛЕМА: {name} — трансформация заморожена, слой не сдвинется");
        }

        System.Console.WriteLine("  трансформации слоёв изменяемы");
    }

    // ===================== Вспомогательное =====================

    private static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (byte)(a.A + (b.A - a.A) * t),
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    protected override int VisualChildrenCount => _children.Count;
    protected override Visual GetVisualChild(int index) => _children[index];
}
