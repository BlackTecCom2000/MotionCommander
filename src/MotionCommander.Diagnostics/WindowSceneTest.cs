using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Win11CopyDialog.Controls;

namespace MotionCommander.Diagnostics;

/// <summary>
/// Проверка космической сцены внутри настоящего окна.
/// </summary>
/// <remarks>
/// <para><b>Что закрывает.</b> До этой проверки сцена проверялась вне
/// окна, и это ограничение было существенным:</para>
///
/// <list type="bullet">
/// <item>вне окна не наступает <c>SizeChanged</c>, из-за чего слои
/// оставались пустыми — и оставались три релиза подряд, пока сцена не
/// была наконец нарисована и рассмотрена;</item>
/// <item>курсор считывается с <c>Window.GetWindow(this)</c>, а вне окна
/// такого хозяина нет, поэтому реакция на движение мыши не проверялась
/// вообще;</item>
/// <item>видимость, состояние окна и отказ анимации при высокой
/// контрастности тоже зависят от окна.</item>
/// </list>
///
/// <para><b>Как устроено.</b> Создаётся настоящее окно с настоящей
/// компоновкой, сцена помещается в него, затем синтезируется движение
/// указателя и измеряется смещение слоя. Указатель не перемещается
/// системно: вызывается обработчик напрямую, потому что системный курсор
/// в диагностическом процессе недоступен, а проверяется именно
/// преобразование координат в целевое смещение.</para>
///
/// <para>Окно не показывается на экране — оно создаётся, компонуется и
/// отрисовывается в прямоугольник пикселей. Всё, что зависит от окна,
/// при этом работает: хозяин найден, компоновка выполнена, размеры
/// настоящие.</para>
/// </remarks>
internal static class WindowSceneTest
{
    /// <summary>Ширина окна проверки, пиксели.</summary>
    private const int Width = 1280;

    /// <summary>Высота окна проверки, пиксели.</summary>
    private const int Height = 800;

    /// <summary>
    /// Число кадров, даваемых окну на обработку очереди перед замером.
    /// </summary>
    /// <remarks>
    /// Компоновка и загрузка WPF выполняются асинхронно: событие
    /// Loaded приходит через очередь диспетчера. Несколько пустых
    /// тактов очереди дают окну завершить компоновку до замера, иначе
    /// элемент оказывается нулевого размера и проверка сообщила бы
    /// о пустой сцене там, где сцена исправна.
    /// </remarks>
    private const int DispatcherTicks = 5;

    /// <summary>
    /// Выполняет проверку. Возвращает число несоответствий.
    /// </summary>
    public static int Run(TextWriter w)
    {
        w.WriteLine("── СЦЕНА В ОКНЕ");
        var problems = 0;

        var scene = new CosmicBackdrop();
        var window = new Window
        {
            Width = Width,
            Height = Height,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Background = Brushes.Black,

            // Ноль непрозрачности вместо того, чтобы не показывать окно
            // вовсе. Окно обязано быть показано: пока оно не показано,
            // компоновка содержимого не выполняется, размер элемента
            // остаётся нулевым, событие изменения размера не наступает —
            // и проверка измеряет ровно ту пустую сцену, ради устранения
            // которой и затевалась.
            Opacity = 0,

            Content = scene
        };

        try
        {
            window.Show();
            PumpDispatcher();
            PumpDispatcher();

            if (window.Content is not Visual visual)
            {
                w.WriteLine("  ПРОБЛЕМА: окно не содержит элемента");
                return 1;
            }

            // Слои должны быть заполнены: в окне SizeChanged наступает,
            // и это главное, что отличало пустую сцену от рабочей.
            w.WriteLine($"  размер элемента: {scene.ActualWidth:0} x {scene.ActualHeight:0}");
            w.WriteLine($"  хозяин найден: {Window.GetWindow(scene) != null}");

            var rendered = RenderVisualToInk(visual, scene.ActualWidth, scene.ActualHeight);
            w.WriteLine($"  заполнено пикселями: {rendered:F1}% площади");

            if (rendered < 1.0)
            {
                w.WriteLine("  ПРОБЛЕМА: в окне сцена пуста");
                problems++;
            }

            // Параллакс: указатель перемещается в левый верхний угол,
            // затем в правый нижний, и смещение слоя обязано измениться.
            double before = scene.StarLayerOffsetForTest;
            MovePointer(scene, 10, 10);
            for (int i = 0; i < 20; i++) scene.RunOneFrameForTest(1.0 / 60.0);
            double atTopLeft = scene.StarLayerOffsetForTest;

            MovePointer(scene, scene.ActualWidth - 10, scene.ActualHeight - 10);
            for (int i = 0; i < 20; i++) scene.RunOneFrameForTest(1.0 / 60.0);
            double atBottomRight = scene.StarLayerOffsetForTest;

            w.WriteLine($"  параллакс: смещение {before:0.000} -> " +
                        $"{atTopLeft:0.000} (угол) -> {atBottomRight:0.000} (низ)");

            if (Math.Abs(atTopLeft - atBottomRight) < 1e-9)
            {
                w.WriteLine("  ПРОБЛЕМА: перемещение указателя не сдвигает слои");
                problems++;
            }
            else
            {
                w.WriteLine("  слои следуют за указателем");
            }

            problems += Capture(w, scene, visual, window);
        }
        catch (Exception ex)
        {
            w.WriteLine($"  ИСКЛЮЧЕНИЕ: {ex.GetType().Name}: {ex.Message}");
            problems++;
        }
        finally
        {
            window.Content = null;
            window.Close();
        }

        w.WriteLine();
        return problems;
    }

    /// <summary>
    /// Задаёт положение указателя для проверки параллакса.
    /// </summary>
    /// <remarks>
    /// Вызывается <c>SetPointerOffset</c> — тот же метод, к которому
    /// обращается обработчик движения мыши. Обработчик напрямую
    /// вызвать нельзя: <c>MouseEventArgs.GetPosition</c> читает
    /// состояние устройства ввода, а не переданное значение, поэтому
    /// подставленная точка вернула бы ноль и проверка сообщила бы о
    /// неработающем параллаксе там, где он исправен.
    /// </remarks>
    private static void MovePointer(CosmicBackdrop scene, double x, double y)
        => scene.SetPointerOffset(x, y);

    private static void PumpDispatcher()
    {
        for (int i = 0; i < DispatcherTicks; i++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    private static double RenderVisualToInk(Visual visual, double width, double height)
    {
        int w = (int)Math.Round(width);
        int h = (int)Math.Round(height);
        if (w < 8 || h < 8) return 0;

        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        return Ink.Measure(bitmap, w, h);
    }

    /// <summary>
    /// Сохраняет снимок окна рядом с программой и проверяет, что файл
    /// действительно что-то содержит.
    /// </summary>
    private static int Capture(TextWriter w, CosmicBackdrop scene, Visual visual, Window window)
    {
        int w2 = (int)Math.Round(window.ActualWidth);
        int h2 = (int)Math.Round(window.ActualHeight);
        if (w2 < 8 || h2 < 8) return 0;

        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            w2, h2, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        string path = Path.Combine(
            AppContext.BaseDirectory, "scene-in-window.png");

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));

        using (var stream = File.Create(path))
            encoder.Save(stream);

        long size = new FileInfo(path).Length;
        w.WriteLine($"  снимок окна: {size / 1024.0:0.0} КБ -> {Path.GetFileName(path)}");

        if (size < 1024)
        {
            w.WriteLine("  ПРОБЛЕМА: снимок окна подозрительно мал");
            return 1;
        }

        return 0;
    }
}
