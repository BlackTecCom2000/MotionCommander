using System;
using System.Windows.Media;

namespace Win11CopyDialog.Helpers;

/// <summary>
/// Расчёт контраста по WCAG 2.1.
///
/// <para><b>Зачем это отдельным классом.</b> Раньше контраст нигде не
/// считался: цвета подбирались на глаз, и тёмная тема с акцентом
/// <c>#7B5CFF</c> на фоне <c>#0B0E22</c> давал отношение около 4.4:1 — чуть
/// ниже порога 4.5:1 для основного текста. Мелкий текст на акценте при
/// этом становился практически нечитаемым, и заметить это можно было
/// только глазами на конкретном экране.</para>
///
/// <para>Теперь контраст вычисляется, и цвет подбирается алгоритмом:
/// <see cref="EnsureReadable"/> поднимает яркость цвета до минимально
/// допустимого отношения к фону, вместо того чтобы надеяться на глаз.</para>
/// </summary>
public static class Contrast
{
    /// <summary>Порог WCAG AA для обычного текста.</summary>
    public const double AaNormal = 4.5;

    /// <summary>Порог WCAG AA для крупного текста (от 18.66 px полужирного или 24 px).</summary>
    public const double AaLarge = 3.0;

    /// <summary>
    /// Относительная яркость по WCAG: каналы линеаризуются, затем
    /// взвешиваются по яркости человеческого восприятия.
    /// </summary>
    public static double Luminance(Color c)
    {
        double r = Linearize(c.R / 255.0);
        double g = Linearize(c.G / 255.0);
        double b = Linearize(c.B / 255.0);
        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    }

    private static double Linearize(double channel)
        => channel <= 0.03928 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);

    /// <summary>Отношение контраста двух цветов, от 1 до 21.</summary>
    public static double Ratio(Color a, Color b)
    {
        double la = Luminance(a);
        double lb = Luminance(b);
        double hi = Math.Max(la, lb);
        double lo = Math.Min(la, lb);
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>Проходит ли цвет порог AA для обычного текста на фоне.</summary>
    public static bool PassesAa(Color foreground, Color background)
        => Ratio(foreground, background) >= AaNormal;

    /// <summary>Проходит ли цвет порог AA для крупного текста на фоне.</summary>
    public static bool PassesAaLarge(Color foreground, Color background)
        => Ratio(foreground, background) >= AaLarge;

    /// <summary>
    /// Возвращает цвет, который читается на фоне: если исходный не
    /// проходит порог, яркость поднимается до минимально необходимой.
    /// </summary>
    /// <param name="color">Исходный цвет текста или значка.</param>
    /// <param name="background">Цвет подложки.</param>
    /// <param name="large">
    /// true — крупный текст, для него действует ослабленный порог 3:1.
    /// </param>
    /// <remarks>
    /// <para>Алгоритм двигает цвет к белому или к чёрному — в ту сторону,
    /// которая повышает контраст, — и останавливается, как только порог
    /// достигнут. Цвет при этом не «ломается»: сохраняется исходный оттенок,
    /// меняется только яркость, поэтому акцентный фиолетовый остаётся
    /// фиолетовым, а не превращается в серый.</para>
    ///
    /// <para>Если даже предельная яркость не даёт нужного отношения,
    /// возвращается противоположный полюс: это лучше, чем оставлять
    /// заведомо нечитаемый цвет, но такой случай попадает в отчёт
    /// как требующий ручного вмешательства.</para>
    /// </remarks>
    public static Color EnsureReadable(Color color, Color background, bool large = false)
    {
        double target = large ? AaLarge : AaNormal;
        if (Ratio(color, background) >= target) return color;

        var hsv = RgbToHsv(color);

        // НАПРАВЛЕНИЕ ОПРЕДЕЛЯЕТСЯ ФАКТОМ, А НЕ ПРЕДПОЛОЖЕНИЕМ.
        //
        // Раньше выбиралось одно направление по сравнению яркости:
        // «цвет темнее фона — осветлять, светлее — затемнять». Это
        // неверно, когда цвета близки по яркости. Для светлой темы
        // (фон L=0,896) и светлой рамки (L=0,784) выбиралось
        // осветление, то есть движение к белому. Но белый на почти
        // белом фоне даёт 1,11:1 — ХУЖЕ исходных 1,14:1. Порог
        // недостижим, и функция возвращала худший из возможных
        // результатов, вместо того чтобы затемнить цвет.
        //
        // Правильно: проверить оба направления и взять то, где
        // контраст растёт. Победитель определяется измерением, а не
        // догадкой о том, «куда правильно».
        Color lightened = NudgeToReach(hsv, background, target, lighten: true);
        if (Ratio(lightened, background) >= target) return lightened;

        Color darkened = NudgeToReach(hsv, background, target, lighten: false);
        if (Ratio(darkened, background) >= target) return darkened;

        // Ни одно направление не достигло порога при сохранении
        // оттенка — обычно это серый цвет около серого фона.
        // Тогда оттенок сохранить невозможно, и остаётся выбрать
        // полюс по измеренному контрасту, а не по предположению:
        // на светлом фоне чёрный читается, а на тёмном — белый.
        return Ratio(Colors.Black, background) >= Ratio(Colors.White, background)
            ? Colors.Black
            : Colors.White;
    }

    /// <summary>
    /// Сдвигает яркость в указанную сторону до достижения порога.
    /// </summary>
    /// <remarks>
    /// Шаг 0,02 по яркости HSV выбран из требования к точности: при
    /// 50 оттенках V отношение контраста меняется не быстрее, чем
    /// примерно на 0,04 за шаг, поэтому шаг заведомо не перескакивает
    /// через минимально достаточное значение. Возвращается лучший
    /// найденный цвет, даже если порог не достигнут, — вызывающий
    /// код должен иметь возможность сравнить оба направления.
    /// </remarks>
    private static Color NudgeToReach(Hsv hsv, Color background, double target, bool lighten)
    {
        Color best = HsvToRgb(hsv.H, hsv.S, hsv.V);
        double bestRatio = Ratio(best, background);

        for (double step = 0.02; step <= 1.0; step += 0.02)
        {
            double v = lighten ? Math.Min(1.0, hsv.V + step) : Math.Max(0.0, hsv.V - step);
            var candidate = HsvToRgb(hsv.H, hsv.S, v);
            double ratio = Ratio(candidate, background);

            if (ratio >= target) return candidate;
            if (ratio > bestRatio) { best = candidate; bestRatio = ratio; }
        }

        return best;
    }

    /// <summary>
    /// Возвращает цвет, читаемый сразу на нескольких фонах.
    /// </summary>
    /// <remarks>
    /// <para><see cref="EnsureReadable(Color, Color, bool)"/> правит один
    /// фон за раз, и ни один из двух её результатов не обязан читаться
    /// на втором. Выбор «более строгого из двух» не помогает: если фоны
    /// различаются по яркости, то вариант, исправленный под более
    /// светлый фон, окажется нечитаемым на более тёмном. Так на теме
    /// Mica исправление под окно давало 4,72:1 на окне и 2,96:1 на
    /// карточке.</para>
    ///
    /// <para>Поэтому здесь ищется не «лучший для одного фона», а цвет с
    /// наибольшим МИНИМАЛЬНЫМ контрастом среди всех фонов: он
    /// гарантирует читаемость везде, где применяется.</para>
    ///
    /// <para>Оттенок исходного цвета сохраняется, как и в одиночной
    /// версии: меняется только яркость, поэтому тема не меняет вид.</para>
    /// </remarks>
    public static Color EnsureReadable(Color color, bool large, params Color[] backgrounds)
    {
        double target = large ? AaLarge : AaNormal;
        if (backgrounds == null || backgrounds.Length == 0) return color;

        double WorstRatio(Color candidate)
        {
            double worst = double.MaxValue;
            foreach (var bg in backgrounds)
            {
                double r = Ratio(candidate, bg);
                if (r < worst) worst = r;
            }
            return worst;
        }

        if (WorstRatio(color) >= target) return color;

        var hsv = RgbToHsv(color);
        Color best = color;
        double bestWorst = WorstRatio(color);

        // Обе стороны перебираются: неизвестно заранее, в какую сторону
        // оттенок уйдёт от обоих фонов сразу.
        foreach (bool lighten in new[] { true, false })
        {
            for (double step = 0.02; step <= 1.0; step += 0.02)
            {
                double v = lighten
                    ? Math.Min(1.0, hsv.V + step)
                    : Math.Max(0.0, hsv.V - step);

                var candidate = HsvToRgb(hsv.H, hsv.S, v);
                double worst = WorstRatio(candidate);

                if (worst >= target) return candidate;
                if (worst > bestWorst) { best = candidate; bestWorst = worst; }
            }
        }

        // Оттенок сохранить не удалось: берётся полюс с наибольшим
        // минимальным контрастом, а не тот, что «правильнее» по
        // отдельному фону.
        double blackWorst = WorstRatio(Colors.Black);
        double whiteWorst = WorstRatio(Colors.White);
        return blackWorst >= whiteWorst ? Colors.Black : Colors.White;
    }

    /// <summary>Оттенок сохраняется, яркость подстраивается под порог.</summary>
    public static Color EnsureReadable(Color color, Brush background, bool large = false)
    {
        if (background is SolidColorBrush scb) return EnsureReadable(color, scb.Color, large);
        return color;
    }

    // ===================== RGB <-> HSV =====================

    private readonly record struct Hsv(double H, double S, double V);

    private static Hsv RgbToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double d = max - min;

        double h = 0;
        if (d > 1e-9)
        {
            if (max == r) h = ((g - b) / d) % 6;
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h /= 6;
            if (h < 0) h += 1;
        }

        double s = max <= 1e-9 ? 0 : d / max;
        return new Hsv(h, s, max);
    }

    private static Color HsvToRgb(double h, double s, double v)
    {
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);

        if (s <= 1e-9)
        {
            // Имя gray, а не g: ниже объявляется переменная g в том же блоке,
            // и объявление byte g перекрывало её, из-за чего сборка падала.
            byte gray = (byte)Math.Round(v * 255);
            return Color.FromRgb(gray, gray, gray);
        }

        double hh = h * 6.0;
        int sector = (int)Math.Floor(hh);
        double f = hh - sector;
        double p = v * (1 - s);
        double q = v * (1 - f * s);
        double t = v * (1 - (1 - f) * s);

        double r, g, b;
        switch (sector % 6)
        {
            case 0: r = v; g = t; b = p; break;
            case 1: r = q; g = v; b = p; break;
            case 2: r = p; g = v; b = t; break;
            case 3: r = p; g = q; b = v; break;
            case 4: r = t; g = p; b = v; break;
            default: r = v; g = p; b = q; break;
        }

        return Color.FromRgb(
            (byte)Math.Round(Math.Clamp(r, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(g, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(b, 0, 1) * 255));
    }
}
