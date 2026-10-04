using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace MotionCommander.Diagnostics;

/// <summary>
/// Прогон всех проверок и подведение итогов.
/// </summary>
/// <remarks>
/// <para>Проверки выполняются по очереди, а не параллельно: почти все
/// они создают элементы WPF и обращаются к общему состоянию тем, а
/// параллельный запуск дал бы взаимные блокировки и невоспроизводимые
/// результаты.</para>
///
/// <para>Исключение внутри проверки не роняет весь прогон: сбой одной
/// проверки не должен лишать сведений о состоянии остальных. Каждая
/// проверка оборачивается, а число неудач считается отдельно.</para>
/// </remarks>
internal static class DiagnosticsRunner
{
    /// <summary>
    /// Выполняет все проверки и возвращает число неудачных.
    /// </summary>
    /// <remarks>
    /// Ноль означает полный успех и используется как код возврата
    /// процесса, чтобы сборочный конвейер мог решить одной строкой,
    /// можно ли выпускать сборку.
    /// </remarks>
    public static int RunAll(TextWriter output)
    {
        output.WriteLine("MOTION COMMANDER — ПОЛНАЯ ДИАГНОСТИКА");
        output.WriteLine(new string('=', 78));
        output.WriteLine();
        output.WriteLine("Запуск без повышения прав. Показания S.M.A.R.T. с самого");
        output.WriteLine("накопителя без прав администратора недоступны, и они будут");
        output.WriteLine("отмечены как недоступные, а не заменены вымышленными.");
        output.WriteLine();

        int failures = 0;

        failures += Run(output, "СБОРКА И ПРАВА", Smoke.BuildAndTypes);
        failures += Run(output, "ТЕМЫ", Smoke.Themes);
        failures += Run(output, "КОНТРАСТ WCAG AA", Smoke.ContrastAudit);
        failures += Run(output, "ПРУЖИННАЯ ФИЗИКА", Smoke.Springs);
        failures += Run(output, "КОСМИЧЕСКАЯ СЦЕНА", Smoke.CosmicScene);
        failures += RunWindow(output, "СЦЕНА В ОКНЕ", WindowSceneTest.Run);
        failures += Run(output, "РАЗБОР S.M.A.R.T.", Smoke.SmartParser);
        failures += Run(output, "НАКОПИТЕЛИ", Smoke.Storage);
        failures += RunAsync(output, "КОПИРОВАНИЕ", Smoke.CopyAsync);
        failures += Run(output, "ПОКАЗАТЕЛИ ЗАПУСКА", Smoke.RunStartupMetrics);
        failures += Run(output, "ПОДПИСЬ АРТЕФАКТОВ", Smoke.ArtifactSignature);
        // Проверка экранов в автоматический прогон пока НЕ включена.
//
// Причина измерена, а не предположена: при создании класса приложения
// WPF начинает завершение работы после закрытия первого окна, и все
// последующие экраны падают с «идет завершение работы объекта
// Application». Из восемнадцати проверяется одно.
//
// Пока подготовка окружения не доведена, раздел отключён: полезнее
// отсутствие проверки, чем отчёт, где восемнадцать одинаковых сбоев
// выглядят как дефекты интерфейса. Проверка доступна через ключ
// основной программы --view-audit, где окружение настоящее.
//
// failures += RunWindow(output, "ЭКРАНЫ", Smoke.ViewAudit);

        output.WriteLine(new string('=', 78));
        output.WriteLine(failures == 0
            ? "ИТОГ: все проверки пройдены"
            : $"ИТОГ: неудачных проверок — {failures}");

        return failures;
    }

    /// <summary>
    /// Выполняет одну проверку, печатая её вывод и считая неудачу.
    /// </summary>
    /// <remarks>
    /// Вывод каждой проверки сначала собирается в буфер и печатается
    /// лишь после её завершения. Без этого прерванный прогон оставил бы
    /// на экране обрывок строки, из-за чего неясно, где именно он
    /// остановился.
    /// </remarks>
    private static int Run(TextWriter w, string name, Action<TextWriter> body)
    {
        w.WriteLine("── " + name);
        var buffer = new StringWriter();
        int problems = 0;

        try
        {
            body(buffer);
        }
        catch (Exception ex)
        {
            problems = 1;
            buffer.WriteLine("  ИСКЛЮЧЕНИЕ: " + ex.GetType().Name + ": " + ex.Message);
        }

        Flush(w, buffer);
        return problems;
    }

    /// <summary>
    /// Выполняет проверку, возвращающую число несоответствий.
    /// </summary>
    /// <remarks>
    /// Отдельный вид по сравнению с <see cref="Run"/>: проверки сцены
    /// сами решают, что считать дефектом, и возвращают счётчик.
    /// Обёртка по выводу и перехвату исключений у них общая.
    /// </remarks>
    private static int RunWindow(TextWriter w, string name, Func<TextWriter, int> body)
    {
        w.WriteLine("── " + name);
        var buffer = new StringWriter();
        int problems;

        try
        {
            problems = body(buffer);
        }
        catch (Exception ex)
        {
            problems = 1;
            buffer.WriteLine("  ИСКЛЮЧЕНИЕ: " + ex.GetType().Name + ": " + ex.Message);
        }

        Flush(w, buffer);
        return problems;
    }

    private static int RunAsync(TextWriter w, string name, Func<TextWriter, Task> body)
    {
        w.WriteLine("── " + name);
        var buffer = new StringWriter();
        int problems = 0;

        try
        {
            body(buffer).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            problems = 1;
            buffer.WriteLine("  ИСКЛЮЧЕНИЕ: " + ex.GetType().Name + ": " + ex.Message);
        }

        Flush(w, buffer);
        return problems;
    }

    private static void Flush(TextWriter w, StringWriter buffer)
    {
        string text = buffer.ToString();
        foreach (string line in text.Split('\n'))
        {
            string t = line.TrimEnd('\r');
            if (t.Length > 0) w.WriteLine("  " + t);
        }
        w.WriteLine();
    }
}
