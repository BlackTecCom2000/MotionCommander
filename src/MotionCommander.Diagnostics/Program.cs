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
            return 0;
        }

        int failures = DiagnosticsRunner.RunAll(autoplay);
        autoplay.Flush();
        return failures;
    }
}
