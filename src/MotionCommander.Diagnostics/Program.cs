using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MotionCommander.Diagnostics;

/// <summary>
/// Точка входа диагностического инструмента.
/// </summary>
/// <remarks>
/// <para>Код возврата совпадает с числом неудачных проверок, поэтому в
/// сборочном конвейере достаточно одного сравнения: ноль означает, что
/// всё в порядке. Проверки печатаются в консоль по-русски и идут в
/// кодировке UTF-8 без перевода строк — вывод должен читаться и в
/// PowerShell, и в журнале сборки.</para>
///
/// <para>Поток помечен как STA, потому что проверки создают элементы
/// WPF: сцену и её слои. В однопоточном апартаменте WPF работает
/// предсказуемо, иначе проверка анимации зависла бы на создании
/// визуальных элементов.</para>
/// </remarks>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Вывод настраивается ДО любых проверок: иначе первая же
        // строчка с кириллицей превратилась бы в знаки вопроса.
        Console.OutputEncoding = new UTF8Encoding(false);

        // Вывод сразу, а не по требованию: если проверка зависнет,
        // по журналу будет видно, на какой именно она остановилась.
        var autoplay = Console.IsOutputRedirected
            ? TextWriter.Synchronized(Console.Out)
            : Console.Out;

        // Прогон всех экранов отдельно от полной диагностики.
        // Отдельный режим нужен, чтобы перепроверять один этот раздел
        // без ожидания всех остальных: он единственный, кто создаёт
        // восемнадцать окон и потому чаще остальных натыкается на
        // состояние окружения.
        // Имя отличается от --view-audit основной программы НАМЕРЕННО.
        //
        // Измерено: при совпадении App.OnStartup видит ключ в аргументах
        // СВОЕГО процесса и поднимает второй прогон, пока идёт первый.
        // Второй стартует после Application.Shutdown() из первого, поэтому
        // все его экраны падают с «идет завершение работы объекта
        // Application» — ложные дефекты, которых в программе нет.
        if (Array.IndexOf(args, "--screens") >= 0)
        {
            return Smoke.ViewAudit(Console.Out);
        }

        // Проба ресурсов: показывает, что реально загрузилось.
        // Нужна, когда экраны падают с «не найден ресурс»: без неё
        // пришлось бы угадывать, чего не хватило.
        if (Array.IndexOf(args, "--res-probe") >= 0)
        {
            return Smoke.ProbeResources();
        }

        // Отчёт S.M.A.R.T. с повышенных прав.
        //
        // Обрабатывается до всех остальных режимов: он запускает
        // второй процесс и ждёт его, поэтому должен проверяться первым.
        if (Array.IndexOf(args, "--smart-report") >= 0 ||
            Array.IndexOf(args, "--smart-elevated") >= 0)
        {
            return SmartReport.Run(args);
        }

        // Отрисовка сцены в файл: --scene-shot <путь> [ширина] [высота]
        int shotAt = Array.IndexOf(args, "--scene-shot");
        if (shotAt >= 0)
        {
            string path = shotAt + 1 < args.Length
                ? args[shotAt + 1]
                : Path.Combine(AppContext.BaseDirectory, "scene.png");

            int w = int.TryParse(shotAt + 2 < args.Length ? args[shotAt + 2] : null, out int pw) ? pw : 0;
            int h = int.TryParse(shotAt + 3 < args.Length ? args[shotAt + 3] : null, out int ph) ? ph : 0;

            return SceneShot.Capture(
                path,
                w > 0 ? w : 1440,
                h > 0 ? h : 900);
        }

        if (Array.IndexOf(args, "--help") >= 0 || Array.IndexOf(args, "-h") >= 0)
        {
            autoplay.WriteLine("Motion Commander — диагностика");
            autoplay.WriteLine();
            autoplay.WriteLine("Запуск: MotionCommanderDiagnostics.exe");
            autoplay.WriteLine();
            autoplay.WriteLine("Проверяет темы, контраст, пружинную физику,");
            autoplay.WriteLine("космическую сцену, разбор S.M.A.R.T. и копирование.");
            autoplay.WriteLine("Прав администратора не требует, поэтому запускается");
            autoplay.WriteLine("в автоматической сборке, где диалог UAC не показывается.");
            autoplay.WriteLine();
            autoplay.WriteLine("Код возврата: 0 — все проверки пройдены, иначе их число.");
            autoplay.WriteLine();
            autoplay.WriteLine("Отрисовка сцены в файл:");
            autoplay.WriteLine("  MotionCommanderDiagnostics.exe --scene-shot <файл.png> [ширина] [высота]");
            autoplay.WriteLine();
            autoplay.WriteLine("Отчёт S.M.A.R.T. с самих накопителей:");
            autoplay.WriteLine("  MotionCommanderDiagnostics.exe --smart-report [файл.txt]");
            autoplay.WriteLine();
            autoplay.WriteLine("Это единственный способ получить настоящие показания");
            autoplay.WriteLine("S.M.A.R.T. вне программы: Windows не открывает накопитель");
            autoplay.WriteLine("обычному пользователю. Появится запрос подтверждения прав.");
            return 0;
        }

        int failures = DiagnosticsRunner.RunAll(autoplay);
        autoplay.Flush();
        return failures;
    }
}
