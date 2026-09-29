using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Win11CopyDialog.Controls;

namespace MotionCommander.Diagnostics;

/// <summary>
/// Отрисовывает космическую сцену в файл изображения.
/// </summary>
/// <remarks>
/// <para>Сцена ни разу не была рассмотрена глазами: она создавалась по
/// коду и проходила проверки на отсутствие исключений, но «не падает» и
/// «выглядит как задумано» — разные утверждения. Единственный способ
/// узнать второе без пользователя — отрисовать её и посмотреть на
/// результат.</para>
///
/// <para>Отрисовка идёт вне окна: элемент измеряется, размещается и
/// рисуется в <see cref="RenderTargetBitmap"/>. Это ровно тот путь, которым
/// сцена идёт в настоящем окне, потому что содержимое слоёв заполняется
/// при изменении размера, а не в цикле перерисовки.</para>
///
/// <para>Кадр анимации выполняется явно, поскольку
/// <c>CompositionTarget.Rendering</c> вне окна событий не выдаёт.</para>
/// </remarks>
internal static class SceneShot
{
    /// <summary>Ширина кадра по умолчанию, пиксели.</summary>
    private const int DefaultWidth = 1440;

    /// <summary>Высота кадра по умолчанию, пиксели.</summary>
    private const int DefaultHeight = 900;

    /// <summary>
    /// Число кадров до снимка.
    /// </summary>
    /// <remarks>
    /// Первый кадр создаёт трансформации слоёв, дальнейшие их двигают.
    /// Снимок делается после нескольких кадров, чтобы попасть в
    /// середину движения: на первом кадре слои стоят в исходном
    /// положении и параллакс ещё не сложился.
    /// </remarks>
    private const int FramesBeforeShot = 30;

    /// <summary>
    /// Рисует сцену и сохраняет в PNG. Возвращает 0 при успехе.
    /// </summary>
    public static int Capture(string path, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            Console.WriteLine("  размер кадра должен быть положительным");
            return 1;
        }

        Console.WriteLine($"ОТРИСОВКА СЦЕНЫ {width}x{height}");
        Console.WriteLine(new string('=', 60));

        var scene = new CosmicBackdrop();
        scene.Measure(new Size(width, height));
        scene.Arrange(new Rect(0, 0, width, height));

        // Перестройка слоёв вызывается явно: вне окна событие
        // изменения размера не наступает, и без этого вызова снимок
        // вышел бы сплошным чёрным.
        scene.RebuildForTest();

        // Шаг кадра равен одной шестидесятой секунды: столько длится
        // кадр при 60 Гц, то есть обычная частота перерисовки.
        const double FrameSeconds = 1.0 / 60.0;
        for (int i = 0; i < FramesBeforeShot; i++)
        {
            try
            {
                scene.RunOneFrameForTest(FrameSeconds);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ПРОБЛЕМА: кадр {i + 1} — {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
        }

        // Масштаб 1:1. Увеличение для просмотра исказило бы мелкие
        // элементы, а их читаемость как раз и требуется оценить.
        var bitmap = new RenderTargetBitmap(
            width, height, 96, 96, PixelFormats.Pbgra32);

        try
        {
            bitmap.Render(scene);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ПРОБЛЕМА: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full) ?? ".");
        using (var stream = File.Create(full))
        {
            encoder.Save(stream);
        }

        var info = new FileInfo(full);
        Console.WriteLine($"  кадров до снимка: {FramesBeforeShot}");
        Console.WriteLine($"  сохранено: {full}");
        Console.WriteLine($"  размер файла: {info.Length / 1024.0:0.0} КБ");

        if (info.Length < 1024)
        {
            // Меньше килобайта для заполненного кадра — признак того,
            // что сцена отрисовалась пустой, и такой снимок бесполезен.
            Console.WriteLine("  ПРОБЛЕМА: файл подозрительно мал, сцена вероятно пуста");
            return 1;
        }

        return 0;
    }
}
