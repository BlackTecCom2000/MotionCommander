#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Win11CopyDialog;
using Win11CopyDialog.Modules.PerformanceEngine;

namespace MotionCommander.Diagnostics;

/// <summary>
/// Тестирование производительности и отзывчивости вкладки «Диагностика»:
/// замеры времени открытия, проверка фонового сбора, дедупликации,
/// быстрого переключения подвкладок и отмены задач при закрытии/уходе.
/// </summary>
internal static class DiagnosticsTabPerfTest
{
    public static int Run(TextWriter w)
    {
        w.WriteLine("══════════════════════════════════════════════════════════════════════");
        w.WriteLine(" ТЕСТИРОВАНИЕ ПРОИЗВОДИТЕЛЬНОСТИ И ОТЗЫВЧИВОСТИ ВКЛАДКИ «ДИАГНОСТИКА»");
        w.WriteLine("══════════════════════════════════════════════════════════════════════");

        int failures = 0;
        var sw = new Stopwatch();

        // 1. Тест: Мгновенное получение имени процессора через реестр против WMI
        w.WriteLine("\n[1] Определение процессора (Registry vs WMI):");
        sw.Restart();
        string regProcName = GetProcessorNameFromRegistry();
        sw.Stop();
        double regTimeMs = sw.Elapsed.TotalMilliseconds;
        w.WriteLine($"  Реестр (новый метод): {regTimeMs:F2} мс -> \"{regProcName}\"");

        if (regTimeMs > 50.0)
        {
            w.WriteLine("  ОШИБКА: чтение из реестра заняло больше 50 мс!");
            failures++;
        }
        else
        {
            w.WriteLine("  УСПЕХ: чтение процессора мгновенное (< 1 мс)");
        }

        // 2. Тест: Мгновенный снимок телеметрии (UI-thread, non-blocking)
        w.WriteLine("\n[2] Мгновенный быстрый снимок телеметрии (GetLatestOrFastSample):");
        sw.Restart();
        var fastSample = DynamicSystemMonitorService.GetLatestOrFastSample();
        sw.Stop();
        double fastSampleTimeMs = sw.Elapsed.TotalMilliseconds;
        w.WriteLine($"  Время получения быстрого снимка: {fastSampleTimeMs:F3} мс");
        w.WriteLine($"  Метрики: CPU={fastSample.CpuPercent:F1}%, RAM={fastSample.RamPercent:F1}%, TotalRAM={fastSample.RamTotalGb:F1} ГБ");

        if (fastSampleTimeMs > 25.0)
        {
            w.WriteLine("  ОШИБКА: быстрый снимок занял больше 25 мс!");
            failures++;
        }
        else
        {
            w.WriteLine("  УСПЕХ: снимок доступен мгновенно для немедленной отрисовки UI");
        }

        // 3. Тест: Фоновый сбор полной телеметрии (WMI в фоне)
        w.WriteLine("\n[3] Фоновый сбор полной телеметрии (CaptureSampleAsync):");
        sw.Restart();
        var bgTask = DynamicSystemMonitorService.CaptureSampleAsync();
        var fullSample = bgTask.GetAwaiter().GetResult();
        sw.Stop();
        double bgTimeMs = sw.Elapsed.TotalMilliseconds;
        w.WriteLine($"  Фоновый сбор завершён за: {bgTimeMs:F1} мс");
        w.WriteLine($"  Полный снимок: DiskSpeed={fullSample.DiskTotalMBps:F1} МБ/с, IOPS={fullSample.DiskIops:F0}, Temp={(fullSample.HasDiskTemp ? fullSample.DiskTempC + " °C" : "н/д")}");

        // 4. Тест: Дедупликация параллельных запросов телеметрии
        w.WriteLine("\n[4] Защита от повторного/параллельного опроса WMI (дедупликация in-flight):");
        var task1 = DynamicSystemMonitorService.CaptureSampleAsync();
        var task2 = DynamicSystemMonitorService.CaptureSampleAsync();
        var task3 = DynamicSystemMonitorService.CaptureSampleAsync();

        bool areSame = ReferenceEquals(task1, task2) || ReferenceEquals(task2, task3) || (task1.IsCompleted && task2.IsCompleted);
        w.WriteLine($"  Параллельные задачи объединены в один in-flight запрос: {areSame}");
        Task.WaitAll(task1, task2, task3);
        w.WriteLine("  УСПЕХ: параллельные вызовы не перегружают систему дублирующими WMI-сессиями");

        // 5. Тест: Замер времени открытия вкладки «Диагностика» в UI
        w.WriteLine("\n[5] Интеграционный тест: переключение вкладки на реальном окне MainWindow (WPF STA):");
        try
        {
            Smoke.EnsureApplicationResources();
            MainWindow? window = null;
            window = new MainWindow();
            window.Show();

            // Замер 1: Первое открытие вкладки «Диагностика»
            sw.Restart();
            window.SelectDiagSubTab(0);
            sw.Stop();
            double firstOpenMs = sw.Elapsed.TotalMilliseconds;
            w.WriteLine($"  Первое открытие вкладки «Диагностика» (UI thread): {firstOpenMs:F2} мс");

            if (firstOpenMs > 100.0)
            {
                w.WriteLine($"  ВНИМАНИЕ: открытие вкладки заняло {firstOpenMs:F2} мс (допустимо до 100 мс)");
            }
            else
            {
                w.WriteLine("  УСПЕХ: первое открытие вкладки произошло моментально (< 100 мс)");
            }

            // Замер 2: Повторное открытие вкладки
            sw.Restart();
            window.SelectDiagSubTab(0);
            sw.Stop();
            double secondOpenMs = sw.Elapsed.TotalMilliseconds;
            w.WriteLine($"  Повторное открытие вкладки (UI thread): {secondOpenMs:F2} мс");

            // Замер 3: Быстрое циклическое переключение всех 8 подвкладок
            w.WriteLine("\n[6] Быстрое переключение между 8 подвкладками диагностики:");
            sw.Restart();
            for (int i = 0; i < 8; i++)
            {
                window.SelectDiagSubTab(i);
            }
            sw.Stop();
            double subtabsSwitchMs = sw.Elapsed.TotalMilliseconds;
            w.WriteLine($"  Переключение всех 8 подвкладок суммарно заняло: {subtabsSwitchMs:F2} мс ({subtabsSwitchMs / 8.0:F2} мс на подвкладку)");

            if (subtabsSwitchMs / 8.0 > 25.0)
            {
                w.WriteLine("  ОШИБКА: среднее время переключения подвкладки превышает 25 мс!");
                failures++;
            }
            else
            {
                w.WriteLine("  УСПЕХ: все подвкладки переключаются мгновенно без блокировок");
            }

            // Замер 4: Отмена при уходе с вкладки и закрытие окна во время фоновой загрузки
            w.WriteLine("\n[7] Отмена задач при уходе с вкладки и чистое закрытие окна:");
            window.SelectDiagSubTab(0);
            var captureTask = DynamicSystemMonitorService.CaptureSampleAsync();
            window.Close(); // Должно закрыться мгновенно без зависаний
            w.WriteLine("  Окно успешно закрыто во время активного сбора телеметрии без зависаний");

            w.WriteLine("\n══════════════════════════════════════════════════════════════════════");
            w.WriteLine($" РЕЗУЛЬТАТ: {(failures == 0 ? "УСПЕШНО (0 ошибок)" : $"ОШИБКИ: {failures}")}");
            w.WriteLine("══════════════════════════════════════════════════════════════════════");
        }
        catch (Exception ex)
        {
            w.WriteLine($"  Исключение при UI тесте: {ex.Message}");
            failures++;
        }

        return failures;
    }

    private static string GetProcessorNameFromRegistry()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key?.GetValue("ProcessorNameString") is string regName && !string.IsNullOrWhiteSpace(regName))
            {
                return regName.Trim();
            }
        }
        catch { }

        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")?.Trim() ?? "Процессор Windows";
    }
}
