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

        // Светлее фона — осветляем, темнее — затемняем.
        bool lighten = Luminance(color) < Luminance(background);
        var hsv = RgbToHsv(color);

        for (double step = 0.02; step <= 1.0; step += 0.02)
        {
            var candidate = HsvToRgb(hsv.H, hsv.S, lighten ? Math.Min(1.0, hsv.V + step) : Math.Max(0.0, hsv.V - step));
            if (Ratio(candidate, background) >= target) return candidate;
        }

        // Порог недостижим: возвращаем полюс с максимальным контрастом.
        return lighten ? Colors.White : Colors.Black;
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
