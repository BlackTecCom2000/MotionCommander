using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Win11CopyDialog.Helpers;

/// <summary>
/// Показатели запуска, которые программа измеряет о себе сама.
/// </summary>
/// <remarks>
/// <para><b>Зачем.</b> Пользователь оценивает программу по первым двум
/// секундам: от запуска до первого полезного действия. Пока эти числа не
/// измерены, утверждать что-либо о скорости запуска нельзя — оценка на
/// глаз обычно оптимистичнее реальности в несколько раз.</para>
///
/// <para><b>Что измеряется.</b> Время от старта процесса до первого
/// отрисованного кадра окна, а не до входа в обработчик запуска:
/// обработчик выполняется до того, как пользователь увидит что-либо, и
/// ничего не говорит о времени ожидания.</para>
///
/// <para><b>Где хранится.</b> Рядом с настройками, в доступном для
/// записи каталоге. В Program Files записать нельзя, поэтому запись
/// туда падала бы при каждом запуске из-под прав администратора.</para>
///
/// <para>Замер пишется один раз на запуск и не растёт: файл
/// перезаписывается. Накопление измерений в файле дало бы вводящий в
/// заблуждение максимум за все запуски, который ничего не говорит о
/// последнем.</para>
/// </remarks>
internal static class StartupMetrics
{
    private static readonly object Gate = new();
    private static bool _written;

    /// <summary>
    /// Момент запуска процесса по данным операционной системы.
    /// </summary>
    /// <remarks>
    /// <para>Отсчёт ведётся от времени старта процесса, а не от
    /// обращения к этому классу.</para>
    ///
    /// <para>Разница не косметическая. Секундомер в статическом поле
    /// начинает идти при первом касании типа, то есть уже после
    /// загрузки сборок и создания окна, и показывал 5 мс при запуске,
    /// который на самом деле занял секунды. Число выглядело отличным и
    /// было бессмысленным — хуже, чем отсутствие измерения, потому что
    /// на него можно опереться.</para>
    ///
    /// <para><c>Process.StartTime</c> берётся у самой операционной
    /// системы и потому включает всё: загрузку образов, инициализацию
    /// среды, повышение прав и создание окна.</para>
    /// </remarks>
    private static DateTime ProcessStartUtc => Process.GetCurrentProcess().StartTime.ToUniversalTime();

    /// <summary>
    /// Записывает показатели текущего запуска.
    /// </summary>
    /// <remarks>
    /// Повторные вызовы игнорируются: показатели описывают один запуск,
    /// и запись из нескольких точек перезаписала бы разные измерения
    /// одного и того же запуска.
    /// </remarks>
    public static void Write()
    {
        lock (Gate)
        {
            if (_written) return;
            _written = true;
        }

        try
        {
            var process = Process.GetCurrentProcess();

            // Рабочая память процесса берётся из счётчика, который
            // Windows ведёт для процесса, и потому включает всё: и
            // управляемую кучу, и загруженные сборки, и буферы видео.
            long workingSet = process.WorkingSet64;

            var data = new RunRecord
            {
                StartedUtc = DateTime.UtcNow,

                // Время от старта процесса до первого отрисованного
                // кадра. Именно его человек ждёт после щелчка по значку.
                StartupMilliseconds =
                    (DateTime.UtcNow - ProcessStartUtc).TotalMilliseconds,

                WorkingSetBytes = workingSet,
                ManagedMemoryBytes = GC.GetTotalMemory(false),
                Is64Bit = Environment.Is64BitProcess,
                RuntimeVersion = Environment.Version.ToString(),
                ThreadCount = process.Threads.Count
            };

            string path = AppPaths.MetricsFile;
            AppPaths.EnsureDir(Path.GetDirectoryName(path) ?? AppPaths.WritableDataDirectory);

            File.WriteAllText(
                path,
                JsonSerializer.Serialize(data, Options));
        }
        catch
        {
            // Сбой записи метрик не должен мешать работе программы:
            // это диагностические данные, а не условие запуска.
        }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Запись одного запуска.</summary>
    private sealed class RunRecord
    {
        public DateTime StartedUtc { get; set; }
        public double StartupMilliseconds { get; set; }
        public long WorkingSetBytes { get; set; }
        public long ManagedMemoryBytes { get; set; }
        public bool Is64Bit { get; set; }
        public string? RuntimeVersion { get; set; }
        public int ThreadCount { get; set; }
    }
}