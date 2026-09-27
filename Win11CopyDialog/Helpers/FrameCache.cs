using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Win11CopyDialog.Helpers;

/// <summary>
/// Кэш замороженных кистей для покадровой отрисовки.
///
/// <para>Проблема, которую решает. В анимационных контролах цвет кисти менялся
/// каждый кадр (мерцание звёзд, затухание вспышек, дыхание свечения), поэтому
/// в коде стояло <c>new SolidColorBrush(...); brush.Freeze();</c> прямо внутри
/// OnRender. Для TransferVisualizer это давало порядка 250 аллокаций и 250
/// вызовов Freeze() на кадр: 48 звёзд, до 70 вспышек, пульс, дуги, узлы.
/// Freeze() — самая дорогая операция здесь, она рекурсивно замораживает граф
/// объектов. Итог: постоянная сборка мусора и рывки интерфейса.</para>
///
/// <para>Решение: прозрачность квантуется в 16 шагов, а готовые замороженные
/// кисти складываются в словарь. За кадр создаётся не более 16 кист��й на цвет
/// вместо двухсот, и они переиспользуются между кадрами.</para>
/// </summary>
internal static class FrameBrushCache
{
    /// <summary>Шагов квантования прозрачности. 16 достаточно: глаз не видит разницы.</summary>
    private const int Levels = 16;

    [ThreadStatic] private static Dictionary<long, SolidColorBrush>? _brushCache;
    [ThreadStatic] private static Dictionary<long, Pen>? _penCache;

    private static Dictionary<long, SolidColorBrush> BrushCache => _brushCache ??= new Dictionary<long, SolidColorBrush>(256);
    private static Dictionary<long, Pen> PenCache => _penCache ??= new Dictionary<long, Pen>(128);

    /// <summary>Замораживает кисть один раз. Вызывать только при создании, не в кадре.</summary>
    public static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        if (b.CanFreeze) b.Freeze();
        return b;
    }

    public static Pen FrozenPen(Color c, double thickness, PenLineCap cap = PenLineCap.Flat, PenLineJoin join = PenLineJoin.Miter)
    {
        var p = new Pen(Frozen(c), thickness) { StartLineCap = cap, EndLineCap = cap, LineJoin = join };
        if (p.CanFreeze) p.Freeze();
        return p;
    }

    /// <summary>
    /// Кисть заданного цвета с квантованной прозрачностью. Результат кэшируется,
    /// поэтому повторный вызов с той же прозрачностью аллокаций не создаёт.
    /// </summary>
    public static SolidColorBrush Solid(Color c, double alpha01)
    {
        int a = Quantize(alpha01);
        // Упаковываем цвет и квантованную прозрачность в один long для быстрого ключа.
        long key = ((long)c.R << 32) | ((long)c.G << 24) | ((long)c.B << 16) | (uint)a;
        if (BrushCache.TryGetValue(key, out var found)) return found;

        var brush = Frozen(Color.FromArgb((byte)a, c.R, c.G, c.B));
        BrushCache[key] = brush;
        return brush;
    }

    public static SolidColorBrush Solid(Color c, byte alpha) => Solid(c, alpha / 255.0);

    /// <summary>Перо заданного цвета и прозрачности с кэшированной кистью.</summary>
    public static Pen Pen4(Color c, double alpha01, double thickness,
        PenLineCap cap = PenLineCap.Flat, PenLineJoin join = PenLineJoin.Miter)
    {
        int a = Quantize(alpha01);
        // Толщина округляется до 0.25: глаз не различает 9.0 и 9.2, а кэш
        // не раздувается от плавающих значений, меняющихся каждый кадр.
        int tq = (int)Math.Round(thickness * 4.0);
        long key = ((long)tq << 40) | ((long)c.R << 24) | ((long)c.G << 16) | ((long)c.B << 8) | (uint)a;
        if (PenCache.TryGetValue(key, out var found)) return found;

        var pen = new Pen(Solid(c, a), tq / 4.0) { StartLineCap = cap, EndLineCap = cap, LineJoin = join };
        if (pen.CanFreeze) pen.Freeze();
        PenCache[key] = pen;
        return pen;
    }

    /// <summary>
    /// Перо по готовой кисти. Кэшируется по цвету кисти и толщине, поэтому
    /// повторный вызов внутри кадра аллокаций не создаёт.
    /// </summary>
    public static Pen PenFor(Brush b, double thickness,
        PenLineCap cap = PenLineCap.Flat, PenLineJoin join = PenLineJoin.Miter)
    {
        Color c = b is SolidColorBrush scb ? scb.Color : Colors.Gray;
        double alpha = b is SolidColorBrush s2 ? s2.Color.A / 255.0 : 1.0;
        return Pen4(c, alpha, thickness, cap, join);
    }

    private static int Quantize(double alpha01)
    {
        if (alpha01 <= 0) return 0;
        if (alpha01 >= 1) return 255;
        int level = (int)(alpha01 * Levels);
        if (level > Levels - 1) level = Levels - 1;
        if (level < 0) level = 0;
        return (int)Math.Round(level * 255.0 / (Levels - 1));
    }

    /// <summary>Сброс кэша при смене темы, чтобы старые оттенки не занимали память.</summary>
    public static void Trim()
    {
        _brushCache?.Clear();
        _penCache?.Clear();
    }
}

/// <summary>
/// Кэш отформатированного текста для анимационных контролов.
///
/// <para>FormattedText — одна из самых дорогих операций в WPF-рендере: он
/// разбивает строку на глифы, измеряет и кэширует раскладку. В TransferVisualizer
/// он создавался четыре раза на кадр (два узла, подпись и технический тег),
/// вместе с двумя Typeface и двумя FontFamily. Подписи не меняются кадр за
/// кадром, поэтому результат кэшируется по ключу «строка + кисть + размер».</para>
/// </summary>
internal static class FrameTextCache
{
    private const int MaxEntries = 64;

    [ThreadStatic] private static Dictionary<string, FormattedText>? _cache;
    [ThreadStatic] private static Queue<string>? _order;

    private static Dictionary<string, FormattedText> Cache => _cache ??= new Dictionary<string, FormattedText>(MaxEntries);
    private static Queue<string> Order => _order ??= new Queue<string>(MaxEntries);

    private static readonly Typeface TagTypeface = new(
        new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    private static readonly Typeface LabelTypeface = new(
        new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public static FormattedText Tag(string text, Brush brush, double pixelsPerDip) =>
        Get(text, brush, 9, TagTypeface, CultureInfo.InvariantCulture, pixelsPerDip);

    public static FormattedText Label(string text, Brush brush, double pixelsPerDip) =>
        Get(text, brush, 11, LabelTypeface, CultureInfo.CurrentUICulture, pixelsPerDip);

    private static FormattedText Get(string text, Brush brush, double size, Typeface typeface, CultureInfo culture, double pixelsPerDip)
    {
        string key = text + "|" + size + "|" + (brush is SolidColorBrush scb ? scb.Color.ToString() : brush.GetHashCode().ToString())
                      + "|" + (int)Math.Round(pixelsPerDip);
        if (Cache.TryGetValue(key, out var hit)) return hit;

        var ft = new FormattedText(text, culture, FlowDirection.LeftToRight, typeface, size, brush, pixelsPerDip);

        if (Cache.Count >= MaxEntries)
        {
            string? oldest = Order.Count > 0 ? Order.Dequeue() : null;
            if (oldest != null) Cache.Remove(oldest);
        }
        Cache[key] = ft;
        Order.Enqueue(key);
        return ft;
    }

    public static void Trim()
    {
        _cache?.Clear();
        _order?.Clear();
    }
}
