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
        if (Array.IndexOf(args, "--screens") >= 0)
        {
            return Smoke.ViewAudit(Console.Out);
        }

        // Проверка надежности механизма обновления: патчи, блокировки, откат
        if (Array.IndexOf(args, "--test-update") >= 0)
        {
            return UpdateEngineTest.Run(Console.Out);
        }

        // Проверка системы оптимизации игр: математика кадров, откат реестра,
        // правка конфигов только по существующим ключам.
        if (Array.IndexOf(args, "--test-gametuning") >= 0)
        {
            return GameTuningTest.Run(Console.Out);
        }

        // Чтение состояния игровых настроек и предпросмотр конфигов.
        // Ничего не применяет: диагностика не меняет систему пользователя.
        int gameOptAt = Array.IndexOf(args, "--game-opt");
        if (gameOptAt >= 0)
        {
            string exe = gameOptAt + 1 < args.Length ? args[gameOptAt + 1] : null;
            return GameTuningProbe.Run(Console.Out, exe);
        }

        // Настоящий замер FPS через ETW. Без прав администратора честно
        // отказывает: подставлять числа вместо измерения запрещено.
        int fpsAt = Array.IndexOf(args, "--fps");
        if (fpsAt >= 0)
        {
            string process = fpsAt + 1 < args.Length ? args[fpsAt + 1] : "dwm";
            int seconds = fpsAt + 2 < args.Length && int.TryParse(args[fpsAt + 2], out int parsedSeconds)
                ? parsedSeconds
                : 10;
            return GameTuningProbe.Fps(Console.Out, process, seconds);
        }

        // Проверка ЖИВОГО канала обновления: манифест, контрольные суммы,
        // реальная загрузка патча через зеркала.
        if (Array.IndexOf(args, "--test-update-live") >= 0)
        {
            return UpdateEngineTest.RunLive(Console.Out);
        }

        // Тестирование производительности вкладки «Диагностика»
        if (Array.IndexOf(args, "--test-diagnostics") >= 0)
        {
            return DiagnosticsTabPerfTest.Run(Console.Out);
        }

        // Проба ресурсов: показывает, что реально загрузилось.
        // Нужна, когда экраны падают с «не найден ресурс»: без неё
        // пришлось бы угадывать, чего не хватило.
        if (Array.IndexOf(args, "--res-probe") >= 0)
        {
            return Smoke.ProbeResources();
        }

        // Игровая категория TAJ ARENA: настоящий автопоиск на этой машине.
        if (Array.IndexOf(args, "--arena") >= 0)
        {
            return ArenaProbe.Run(autoplay);
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
            autoplay.WriteLine("Оптимизация игр (только чтение, без изменений):");
            autoplay.WriteLine("  MotionCommanderDiagnostics.exe --game-opt [путь к exe игры]");
            autoplay.WriteLine();
            autoplay.WriteLine("Проверки оптимизации игр:");
            autoplay.WriteLine("  MotionCommanderDiagnostics.exe --test-gametuning");
            autoplay.WriteLine();
            autoplay.WriteLine("Настоящий замер FPS через ETW (нужны права администратора):");
            autoplay.WriteLine("  MotionCommanderDiagnostics.exe --fps <имя процесса|PID> [секунды]");
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
