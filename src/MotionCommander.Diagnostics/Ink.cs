using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MotionCommander.Diagnostics;

/// <summary>
/// Измерение заполненности кадра пикселями.
/// </summary>
/// <remarks>
/// <para><b>Зачем это нужно.</b> Отсутствие исключений ничего не говорит
/// о том, что на экране есть изображение. Космическая сцена три релиза
/// подряд проходила все проверки на стабильность, будучи при этом
/// сплошной чёрной: слои заполняются по событию изменения размера, а вне
/// окна оно не наступает. Число исключений оставалось нулевым при
/// полностью пустом кадре.</para>
///
/// <para>Поэтому заполненность измеряется по пикселям: кадр
/// отрисовывается, каждый пиксель сравнивается с фоном, и доля
/// отличающихся считается. Ноль означает пустую сцену независимо от
/// того, сколько кадров прошло без ошибок.</para>
/// </remarks>
internal static class Ink
{
    /// <summary>
    /// Шаг выборки по каждой оси, пиксели.
    /// </summary>
    /// <remarks>
    /// Два пикля достаточно: сцена состоит из крупных форм — диска,
    /// ореола, звёзд, — а не из тонких линий, и пропуск каждого второго
    /// пикселя на доле заполнения не сказывается. Считать все пиксели
    /// смысла нет: результат от этого не меняется, а работы вчетверо
    /// больше.
    /// </remarks>
    private const int Step = 2;

    /// <summary>
    /// Допуск отличия от фона по каждому каналу.
    /// </summary>
    /// <remarks>
    /// Шесть из 255 — примерно два с половиной процента. Достаточно,
    /// чтобы плавный градиент фона не принимался за изображение, и мало
    /// достаточно, чтобы настоящие объекты — тусклые звёзды, граница
    /// планеты — попадали в счёт.
    /// </remarks>
    private const int Tolerance = 6;

    /// <summary>
    /// Возвращает долю площади кадра, занятую пикселями не фонового
    /// цвета, в процентах.
    /// </summary>
    public static double Measure(BitmapSource bitmap, int width, int height)
    {
        if (width < 8 || height < 8) return 0.0;

        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);

        // Фон — самый частый цвет кадра. Задавать его заранее нельзя:
        // фон сцены задаёт градиент, и любое фиксированное значение дало
        // бы ложный отчёт либо о пустоте, либо о заполненности.
        var counts = new Dictionary<uint, int>();
        for (int y = 0; y < height; y += Step)
        {
            for (int x = 0; x < width; x += Step)
            {
                int i = y * stride + x * 4;
                uint key = (uint)((pixels[i] << 16) | (pixels[i + 1] << 8) | pixels[i + 2]);
                counts.TryGetValue(key, out int n);
                counts[key] = n + 1;
            }
        }

        uint background = 0;
        int best = -1;
        foreach (var pair in counts)
        {
            if (pair.Value > best) { best = pair.Value; background = pair.Key; }
        }

        byte br = (byte)((background >> 16) & 0xFF);
        byte bg = (byte)((background >> 8) & 0xFF);
        byte bb = (byte)(background & 0xFF);

        int total = 0;
        int inked = 0;
        for (int y = 0; y < height; y += Step)
        {
            for (int x = 0; x < width; x += Step)
            {
                int i = y * stride + x * 4;
                total++;
                if (Math.Abs(pixels[i] - br) > Tolerance ||
                    Math.Abs(pixels[i + 1] - bg) > Tolerance ||
                    Math.Abs(pixels[i + 2] - bb) > Tolerance)
                    inked++;
            }
        }

        return total == 0 ? 0.0 : inked * 100.0 / total;
    }
}
