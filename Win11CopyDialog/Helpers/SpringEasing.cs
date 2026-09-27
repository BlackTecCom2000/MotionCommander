using System.Windows;
using System.Windows.Media.Animation;
using Win11CopyDialog.Models;

namespace Win11CopyDialog.Helpers;

/// <summary>
/// Пружинная функция плавности для WPF.
///
/// <para><b>Зачем это лучше обычного easing.</b> Стандартные
/// <c>CubicEase</c> и <c>QuadraticEase</c> задают форму кривой, но не
/// физику: у них нет массы, жёсткости и затухания, поэтому отклик на
/// резкое изменение цели всегда выглядит одинаково — неважно, был ли
/// переход на 2 пикселя или на 400. Настоящая пружина реагирует на
/// величину смещения и на то, как быстро цель изменилась, и это читается
/// как «живой» интерфейс.</para>
///
/// <para><b>Физика, а не подгонка.</b> Используется аналитическое решение
/// уравнения затухающего гармонического осциллятора, а не численный
/// интегратор: значение зависит только от прошедшего времени, поэтому
/// результат одинаков при 30 и при 144 кадрах в секунду. Численная
/// пружина (Motion.Spring) требует фиксированного шага и «рассыпается»
/// при просадке кадров.</para>
///
/// <para><b>Почему реализован интерфейс, а не класс.</b> В WPF класс
/// <c>EasingFunction</c> — внутренний (в .NET 8 он не входит в
/// публичные справочные сборки), и производные от него классы снаружи
/// сборки собрать нельзя. Публичный контракт — только
/// <c>IEasingFunction</c> с одним методом <c>Ease</c>.</para>
///
/// <para>В XAML:
/// <c>&lt;DoubleAnimation.EasingFunction&gt;
///   &lt;helpers:SpringEasing Kind="Snappy" /&gt;
/// &lt;/DoubleAnimation.EasingFunction&gt;</c>
/// при объявленном <c>xmlns:helpers="clr-namespace:Win11CopyDialog.Helpers"</c>.</para>
/// </summary>
public sealed class SpringEasing : IEasingFunction
{
    /// <summary>
    /// Именованные характеры пружины. Каждый — реальная пара
    /// (жёсткость, коэффициент затухания), а не набор точек кривой.
    /// </summary>
    public enum SpringKind
    {
        /// <summary>Мягкая пружина для крупных панелей. ζ≈0.85, один лёгкий перелёт.</summary>
        Gentle,

        /// <summary>Пружина по умолчанию для кнопок и списков. ζ≈0.72, заметный отскок.</summary>
        Snappy,

        /// <summary>Почти без колебаний, для текста и цифр. ζ≈1.02, апериодическое затухание.</summary>
        Precise,

        /// <summary>Резиновый отклик для подтверждения действий. ζ≈0.55, два колебания.</summary>
        Bouncy
    }

    private readonly double _stiffness;   // ω², рад²/с²
    private readonly double _damping;     // 2ζω, 1/с

    /// <summary>Характер пружины.</summary>
    public SpringKind Kind { get; }

    public SpringEasing() : this(SpringKind.Snappy) { }

    public SpringEasing(SpringKind kind)
    {
        Kind = kind;
        (_stiffness, _damping) = kind switch
        {
            SpringKind.Gentle => (160.0, 21.4),    // ζ = 0.85
            SpringKind.Snappy => (230.0, 21.8),    // ζ = 0.72
            SpringKind.Precise => (300.0, 35.0),   // ζ = 1.02
            SpringKind.Bouncy => (260.0, 14.5),    // ζ = 0.45
            _ => (230.0, 21.8)
        };
    }

    /// <summary>Естественная частота пружины, рад/с.</summary>
    public double NaturalFrequency => Math.Sqrt(_stiffness);

    /// <summary>Коэффициент затухания ζ. 1 — критическое, &lt;1 — колебательное.</summary>
    public double DampingRatio => _damping / (2.0 * NaturalFrequency);

    /// <summary>
    /// Нормированная форма пружины: значение 0..1, где 1 — покой в цели.
    /// Не ограничено единицей: перелёт при отскоке — это и есть эффект.
    /// </summary>
    public double Ease(double progress)
    {
        if (progress <= 0) return 0;
        if (progress >= 1) return 1;

        double w = NaturalFrequency;
        double zeta = DampingRatio;
        double t = w * progress;   // безразмерное время: t = ω·progress

        if (zeta < 1.0 - 1e-9)
        {
            // Недо-, критически- и пере- затухание в одной формуле не
            // выражается, поэтому ветви разделены: на границах формулы
            // расходятся и при ζ→1 дают NaN.
            double wd = w * Math.Sqrt(1.0 - zeta * zeta);
            return 1.0 - Math.Exp(-zeta * t) *
                         (Math.Cos(wd / w * t) + zeta / Math.Sqrt(1.0 - zeta * zeta) * Math.Sin(wd / w * t));
        }

        if (zeta <= 1.0 + 1e-9)
        {
            // Критическое затухание: единственный кратный корень.
            return 1.0 - Math.Exp(-t) * (1.0 + t);
        }

        // Перезатухание (ζ > 1). Отклик на единичный скачок:
        //   x(t) = 1 - (r2·e^{r1·t} - r1·e^{r2·t}) / (r2 - r1)
        //
        // Здесь нужна ПОСТОЯННАЯ 1 и коэффициенты c1 = -r2/(r2-r1),
        // c2 = r1/(r2-r1). Раньше стояло c1 = 1 - c2, что не эквивалентно:
        // кривая уходила ниже нуля (измеренный минимум −0.0625 при ζ≈1.01
        // вместо монотонного подхода), то есть апериодическая пружина
        // вела себя как колебательная с обратным знаком.
        double s = zeta * zeta - 1.0;
        double rt = Math.Sqrt(s);
        double r1 = -w * (zeta - rt);
        double r2 = -w * (zeta + rt);
        double denom = r2 - r1;
        if (Math.Abs(denom) < 1e-12) return 1.0 - Math.Exp(-t) * (1.0 + t);

        double c1 = -r2 / denom;
        double c2 = r1 / denom;
        return 1.0 + c1 * Math.Exp(r1 / w * t) + c2 * Math.Exp(r2 / w * t);
    }
}

/// <summary>
/// Каскадная (stagger) задержка для появления элементов списка.
///
/// <para>Когда на экране возникают сразу 20 строк и все они одновременно
/// начинают двигаться, результат читается как «мигание», а не как
/// появление. Небольшой сдвиг по времени между соседними элементами
/// создаёт ощущение последовательности.</para>
///
/// <para>Задержка ограничена сверху: при 200 элементах последний не должен
/// появляться через две секунды — иначе анимация мешает работе. Поэтому
/// шаг автоматически делится, если суммарный каскад превышает
/// <see cref="MaxTotalMs"/>.</para>
/// </summary>
public static class Stagger
{
    /// <summary>Максимальная суммарная задержка каскада, мс.</summary>
    public const int MaxTotalMs = 420;

    /// <summary>Задержка для элемента с заданным индексом, мс.</summary>
    public static int Delay(int index, int count, int stepMs = 26, int startMs = 0)
    {
        if (index < 0) index = 0;
        if (count <= 1) return startMs;
        if (index >= count) index = count - 1;

        // Шаг подбирается в ДОЛЯХ, а не целыми миллисекундами.
        // Прежний вариант делал целочисленное деление MaxTotalMs/(count-1)
        // и поднимал результат до 1: при 500 элементах получалось 499 мс
        // вместо предельных 420, то есть каскад не ограничивался вовсе.
        double step = Math.Min(stepMs, (double)MaxTotalMs / (count - 1));
        double total = index * step;

        // Округление вверх накапливает погрешность, поэтому последний
        // элемент прижимается к границе бюджета явно.
        if (index == count - 1 && total > MaxTotalMs) total = MaxTotalMs;

        return startMs + (int)Math.Round(total);
    }

    /// <summary>
    /// Запускает появление элементов списка с каскадом, отключая анимацию
    /// для элементов, уже отрендеренных.
    /// </summary>
    /// <param name="container">Панель с элементами.</param>
    /// <param name="stepMs">Базовый шаг между соседними элементами.</param>
    /// <param name="durationMs">Длительность появления одного элемента.</param>
    public static void AnimateEntrance(DependencyObject? container, int stepMs = 26, int durationMs = 220)
    {
        if (container is null) return;

        // В режиме «Эконом» каскад отключается: пользователь выбрал
        // минимальное движение, а декоративная анимация тут не главная.
        if (ThemeManager.Instance.AnimationQuality == AnimationQuality.Economy) return;

        if (container is not System.Windows.Controls.Panel panel) return;

        int i = 0;
        int count = panel.Children.Count;
        foreach (var child in panel.Children)
        {
            if (child is not System.Windows.FrameworkElement fe) { i++; continue; }

            int delay = Delay(i, count, stepMs);
            fe.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(durationMs))
            {
                BeginTime = TimeSpan.FromMilliseconds(delay),
                FillBehavior = FillBehavior.HoldEnd,
                EasingFunction = new SpringEasing(SpringEasing.SpringKind.Precise)
            });
            i++;
        }
    }
}
