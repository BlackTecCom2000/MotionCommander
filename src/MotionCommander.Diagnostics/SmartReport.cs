using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Win11CopyDialog.Helpers;
using Win11CopyDialog.Modules.StorageControlCenter.Models;
using Win11CopyDialog.Modules.StorageControlCenter.Services;

namespace MotionCommander.Diagnostics;

/// <summary>
/// Отчёт о состоянии накопителей, читаемый с самих устройств.
/// </summary>
/// <remarks>
/// <para><b>Зачем он нужен.</b> Чтение S.M.A.R.T. напрямую с накопителя
/// требует прав администратора: Windows не открывает
/// <c>\\.\PhysicalDriveN</c> обычному пользователю. Основная программа
/// требует прав всегда, поэтому проверить этот путь из среды сборки
/// невозможно — она не может повысить привилегии, и диалог подтверждения
/// не показывается.</para>
///
/// <para>Этот режим снимает проблему: пользователь запускает одну
/// команду, Windows один раз спрашивает подтверждение, и отчёт
/// получается настоящим — с реального устройства, а не с эталонного
/// блока.</para>
///
/// <para><b>Что важно.</b> Отчёт не подставляет ничего вместо
/// измеренного. Если чтение не удалось, в отчёте так и написано, и
/// причина приведена. Заглушки здесь недопустимы: отчёт о состоянии
/// диска, составленный из правдоподобных чисел, опаснее отсутствия
/// отчёта.</para>
///
/// <para>Результат пишется в файл, потому что повышенный процесс
/// запускается оболочкой в отдельном окне, и его вывод пользователю
/// не виден.</para>
/// </remarks>
internal static class SmartReport
{
    /// <summary>Ключ повышенного запуска.</summary>
    /// <remarks>
    /// Повышенный потомок отмечается отдельным ключом, чтобы отличать
    /// его от первого запуска и не зациклиться: иначе процесс
    /// повысил бы сам себя бесконечно.
    /// </remarks>
    private const string ElevatedKey = "--smart-elevated";

    /// <summary>Ключ запуска отчёта пользователем.</summary>
    private const string RequestKey = "--smart-report";

    /// <summary>Сколько ждём завершения повышенного процесса, миллисекунды.</summary>
    /// <remarks>
    /// Чтение нескольких накопителей с последовательными запросами к
    /// устройству занимает секунды, а при отклике диска — десятки.
    /// Двух минут достаточно; при нехватке отчёт остаётся незавершённым,
    /// и об этом сообщается, а не выдаётся за пустой результат.
    /// </remarks>
    private const int ElevatedWaitMilliseconds = 120_000;

    /// <summary>
    /// Обрабатывает запуск. Возвращает код выхода процесса.
    /// </summary>
    public static int Run(string[] args)
    {
        bool elevated = Array.IndexOf(args, ElevatedKey) >= 0;
        int at = Array.IndexOf(args, RequestKey);

        string outputPath = ResolveOutputPath(args, elevated);

        return elevated
            ? WriteReport(outputPath)
            : RequestElevated(outputPath);
    }

    private static string ResolveOutputPath(string[] args, bool elevated)
    {
        string key = elevated ? ElevatedKey : RequestKey;
        int i = Array.IndexOf(args, key);
        if (i >= 0 && i + 1 < args.Length) return args[i + 1];

        string name = $"smart-report-{DateTime.Now:yyyyMMdd-HHmmss}.txt";
        return Path.Combine(AppPaths.WritableDataDirectory, name);
    }

    /// <summary>
    /// Запрашивает повышение и ждёт результат.
    /// </summary>
    private static int RequestElevated(string outputPath)
    {
        var console = TextWriter.Synchronized(Console.Out);

        console.WriteLine("S.M.A.R.T. — ЧТЕНИЕ С НАКОПИТЕЛЕЙ");
        console.WriteLine(new string('=', 64));
        console.WriteLine();

        if (SuperAdminPrivilegeHelper.IsAdministrator())
        {
            // Права уже есть: повторный запрос показал бы лишний диалог.
            console.WriteLine("Права администратора уже есть, отчёт снимается сразу.");
            return WriteReport(outputPath);
        }

        console.WriteLine("Требуются права администратора: Windows не открывает накопитель");
        console.WriteLine("обычному пользователю. Сейчас появится запрос подтверждения.");
        console.WriteLine();
        console.WriteLine("Отчёт после чтения будет записан сюда:");
        console.WriteLine("  " + outputPath);
        console.WriteLine();

        string exe = Environment.ProcessPath
                     ?? Process.GetCurrentProcess().MainModule?.FileName
                     ?? "MotionCommanderDiagnostics.exe";

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = true,
            Verb = "runas",

            // Рабочий каталог при повышении оболочка подставляет
            // системный, и относительные пути внутри программы
            // разрешились бы неверно, поэтому он не переносится.
            WorkingDirectory = string.Empty
        };

        psi.ArgumentList.Add(ElevatedKey);
        psi.ArgumentList.Add(outputPath);

        Process child;
        try
        {
            child = Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            console.WriteLine("Повышение не выполнено: " + ex.Message);
            if (ex.NativeErrorCode == 1223)
            {
                console.WriteLine("Код 1223 — подтверждение отклонено или диалог недоступен.");
                console.WriteLine("Без прав показания S.M.A.R.T. получить нельзя, и отчёт");
                console.WriteLine("не создаётся: подставлять вместо измерения нечего.");
            }
            return 2;
        }

        if (child == null)
        {
            console.WriteLine("Повышенный процесс не запустился.");
            return 2;
        }

        if (!child.WaitForExit(ElevatedWaitMilliseconds))
        {
            console.WriteLine("Повышенный процесс не завершился за отведённое время.");
            console.WriteLine("Возможно, диск не отвечает на запрос. Отчёт не создан.");
            return 3;
        }

        if (child.ExitCode != 0 || !File.Exists(outputPath))
        {
            console.WriteLine($"Отчёт не создан, код повышенного процесса: {child.ExitCode}.");
            return child.ExitCode == 0 ? 3 : child.ExitCode;
        }

        Console.WriteLine();
        Console.WriteLine(new string('=', 64));
        Console.WriteLine("ОТЧЁТ ПОЛУЧЕН");
        Console.WriteLine();

        // Выводится содержимое файла, а не только путь: иначе
        // пользователю пришлось бы искать результат вручную.
        Console.WriteLine(File.ReadAllText(outputPath));
        return 0;
    }

    /// <summary>
    /// Снимает показания с накопителей и записывает отчёт.
    /// </summary>
    private static int WriteReport(string outputPath)
    {
        var text = new StringBuilder();

        text.AppendLine("S.M.A.R.T. — ОТЧЁТ ПО НАКОПИТЕЛЯМ");
        text.AppendLine(new string('=', 64));
        text.AppendLine();
        text.AppendLine($"Снято:   {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine($"Права:   {(SuperAdminPrivilegeHelper.IsAdministrator() ? "администратор" : "обычные")}");
        text.AppendLine();

        // Различаются три разные ситуации, и их важно не путать:
        // устройство не открылось (нет доступа), открылось, но S.M.A.R.T.
        // не вернулся (контроллер не передаёт), или вернулся.
        // Смешивать их нельзя: первая — вопрос прав, вторая — свойство
        // оборудования, и лечится по-разному.
        text.AppendLine("Проверка прямого доступа к устройству:");
        text.AppendLine();

        int disks = 0;
        int withData = 0;
        int opened = 0;

        foreach (int number in EnumeratePhysicalDisks(text))
        {
            disks++;
            text.AppendLine(new string('-', 64));

            var disk = new StorageDisk
            {
                DiskNumber = number,
                Model = $"PhysicalDrive{number}"
            };

            // Прямая проверка устройства: если оно открывается, значит
            // права достаточны, и отсутствие S.M.A.R.T. объясняется уже
            // не доступом, а устройством. Это различие видно только
            // здесь — в остальных путях отказ сливается с «нет
            // показаний».
            bool deviceOpened = ProbeDevice(number, text);

            // Обе попытки чтения: ATA для обычных накопителей и общая,
            // умеющая NVMe. Если тип накопителя определён неверно,
            // отчёт покажет обе попытки, и будет видно, что дело не в
            // выборе команды.
            ProbeSmart(number, text);

            if (deviceOpened) opened++;

            // Путь тот же, что и в программе: он сам выбирает прямое
            // чтение через IOCTL и только затем запасные источники.
            SmartHealthService.EnrichDiskHealth(disk);

            text.AppendLine($"Накопитель PhysicalDrive{number}");

            if (!disk.HasSmartAttributes)
            {
                text.AppendLine("  S.M.A.R.T.: данных нет.");
                text.AppendLine($"  причина:  {DescribeReason(disk)}");
                text.AppendLine("  Показания не подставляются: вместо измерения");
                text.AppendLine("  выводить ничего нельзя.");
                text.AppendLine();
                continue;
            }

            withData++;
            text.AppendLine($"  атрибутов: {disk.SmartAttributes.Count}");
            if (disk.HasTemperature)
                text.AppendLine($"  температура: {disk.TemperatureC:0.#} °C  ({disk.TemperatureSource})");
            if (disk.PowerOnHours > 0)
                text.AppendLine($"  наработка:  {disk.PowerOnHours} ч");
            if (disk.PowerCycles > 0)
                text.AppendLine($"  циклов включения: {disk.PowerCycles}");
            if (disk.HasWear)
                text.AppendLine($"  износ:      {disk.WearLevelPercent:0.#} %");
            if (disk.ReadErrorsTotal > 0)
                text.AppendLine($"  ошибок чтения: {disk.ReadErrorsTotal}");
            if (disk.WriteErrorsTotal > 0)
                text.AppendLine($"  ошибок записи: {disk.WriteErrorsTotal}");

            text.AppendLine();
            text.AppendLine($"  {"ID",-5} {"Наименование",-38} {"Текущее",8} {"Порог",7} {"Сырое",14}");
            text.AppendLine($"  {new string('-', 76)}");

            foreach (var a in disk.SmartAttributes)
            {
                text.AppendLine(
                    $"  {a.Id,-5} {Trim(a.Name, 38),-38} {a.Current,8} {a.Threshold,7} {a.RawValueFormatted,14}");
            }

            text.AppendLine();
        }

        text.AppendLine(new string('=', 64));
        text.AppendLine($"Накопителей проверено: {disks}, " +
                        $"устройство открыто: {opened}, с показаниями S.M.A.R.T.: {withData}");

        if (withData == 0 && opened > 0)
        {
            text.AppendLine();
            text.AppendLine("ВЫВОД: права достаточны, устройства открываются, но S.M.A.R.T.");
            text.AppendLine("не возвращается.");
            text.AppendLine();
            text.AppendLine("Это свойство оборудования, а не сбой программы. Обычно так");
            text.AppendLine("ведёт себя накопитель за RAID-контроллером, в виртуальной");
            text.AppendLine("машине или в системе с самодостаточным кэшем: контроллер");
            text.AppendLine("разбирает запросы сам и операционной системе ничего не передаёт.");
            text.AppendLine();
            text.AppendLine("Показания не подставляются: правдоподобное значение состояния");
            text.AppendLine("диска хуже, чем честное «данных нет»: по нему принимают");
            text.AppendLine("решения о замене носителя.");
        }
        else if (withData == 0 && opened == 0)
        {
            text.AppendLine();
            text.AppendLine("Ни одно устройство не открылось. Если накопители есть,");
            text.AppendLine("программа запущена без прав администратора.");
        }

        try
        {
            AppPaths.EnsureDir(Path.GetDirectoryName(outputPath) ?? AppPaths.WritableDataDirectory);
            File.WriteAllText(outputPath, text.ToString());
        }
        catch (Exception ex)
        {
            Console.WriteLine("Отчёт не записан: " + ex.Message);
            return 4;
        }

        return 0;
    }

    /// <summary>
    /// Перечисляет физические накопители, доступные в системе.
    /// </summary>
    /// <remarks>
    /// <para>Номера берутся из WMI <c>Win32_DiskDrive.Index</c>: он
    /// совпадает с номером в пути <c>\\.\PhysicalDriveN</c>, который
    /// ожидает чтение S.M.A.R.T., и не зависит от букв разделов —
    /// те меняются при разбиении диска.</para>
    ///
    /// <para><b>Что было неверно.</b> Сначала перечисление шло через
    /// <c>Directory.GetFiles(@"\\.\")</c>. Так перечислить устройства
    /// нельзя: <c>\\.\</c> не является каталогом, и вызов всегда
    /// возвращает синтаксическую ошибку имени. Отчёт при этом
    /// выглядел правдоподобно и утверждал, что накопителей нет, хотя
    /// они есть. На пустом результате устройств отчёт выглядит
    /// правдоподобно и вводит в заблуждение, поэтому при неудаче
    /// перечисления в него попадает явная причина.</para>
    ///
    /// <para>Запасной путь — открыть <c>\\.\PhysicalDriveN</c> для
    /// небольшого набора номеров. Он применяется, только если WMI не
    /// ответил: без него на системе со сломанным WMI отчёт был бы
    /// пустым, хотя права есть и прочитать можно.</para>
    /// </remarks>
    private static IEnumerable<int> EnumeratePhysicalDisks(StringBuilder report)
    {
        var found = new SortedSet<int>();

        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Index FROM Win32_DiskDrive");

            using var results = searcher.Get();
            foreach (System.Management.ManagementBaseObject obj in results)
            {
                using (obj)
                {
                    if (obj["Index"] is uint index)
                        found.Add((int)index);
                }
            }

            report.AppendLine($"  WMI Win32_DiskDrive: найдено устройств — {found.Count}");
        }
        catch (Exception ex)
        {
            report.AppendLine($"  WMI недоступен: {ex.GetType().Name}: {ex.Message}");
            report.AppendLine("  Перечисление переключено на проверку прямого доступа.");
        }

        if (found.Count == 0)
        {
            // Прямая проверка: открывается ли устройство. Пробуется
            // небольшой диапазон, потому что в системе не бывает
            // десятков физических накопителей, а перебирать сотни
            // номеров вслепую бессмысленно.
            const int MaxProbe = 16;
            for (int n = 0; n < MaxProbe; n++)
            {
                try
                {
                    using var stream = new FileStream(
                        $@"\\.\PhysicalDrive{n}", FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite);

                    found.Add(n);
                }
                catch
                {
                    // Устройства нет либо доступ запрещён: это не
                    // ошибка перечисления, а отсутствие накопителя.
                }
            }

            report.AppendLine($"  Прямой доступ: найдено устройств — {found.Count}");
        }

        if (found.Count == 0)
        {
            report.AppendLine("  Физические накопители не перечислены.");
            report.AppendLine("  Если накопители есть, проверьте, что программа");
            report.AppendLine("  запущена с правами администратора.");
        }

        return found;
    }

    /// <summary>
    /// Проверяет, открывается ли устройство напрямую.
    /// </summary>
    /// <remarks>
    /// Успех здесь доказывает, что права достаточны и путь до накопителя
    /// пройден целиком. После этого отсутствие S.M.A.R.T. относится к
    /// устройству, а не к программе: разделить эти два случая иначе
    /// нечем.
    /// </remarks>
    private static bool ProbeDevice(int number, StringBuilder report)
    {
        try
        {
            using var stream = new FileStream(
                $@"\\.\PhysicalDrive{number}", FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite);

            report.AppendLine($"  PhysicalDrive{number}: устройство открыто, права достаточны");
            return true;
        }
        catch (Exception ex)
        {
            report.AppendLine($"  PhysicalDrive{number}: устройство НЕ открыто — " +
                              $"{ex.GetType().Name}: {ex.Message.Split('\n')[0].Trim()}");
            return false;
        }
    }

    /// <summary>
    /// Пробует прочитать S.M.A.R.T. обоими способами и пишет результат.
    /// </summary>
    /// <remarks>
    /// Показаны обе попытки намеренно. Если бы выводился только итог,
    /// отчёт выглядел бы одинаково и при неверно выбранном типе
    /// накопителя, и при устройстве, которое не отдаёт S.M.A.R.T. вовсе.
    /// Различие видно только по попыткам.
    /// </remarks>
    private static void ProbeSmart(int number, StringBuilder report)
    {
        try
        {
            var ata = SmartRawReader.ReadAtaSmart(number, out var ataFail);
            report.AppendLine($"  PhysicalDrive{number}: ATA  — " +
                (ata != null ? "прочитан" : "нет данных: " + ataFail?.Reason));

            var any = SmartRawReader.Read(number, out var anyFail);
            report.AppendLine($"  PhysicalDrive{number}: общее — " +
                (any != null ? "прочитан" : "нет данных: " + anyFail?.Reason));
        }
        catch (Exception ex)
        {
            report.AppendLine($"  PhysicalDrive{number}: проверка S.M.A.R.T. прервана — " +
                              $"{ex.GetType().Name}: {ex.Message.Split('\n')[0].Trim()}");
        }

        report.AppendLine();
    }

    /// <summary>
    /// Объясняет, почему показания не получены.
    /// </summary>
    /// <remarks>
    /// Причина берётся у самого накопителя: служба уже записала её в
    /// <see cref="StorageDisk.TelemetryNote"/>. Свой объяснение
    /// продублировало бы то, что уже известно системе, и могло бы
    /// разойтись с настоящей причиной.
    /// </remarks>
    private static string DescribeReason(StorageDisk disk)
    {
        if (disk.SmartNeedsAdministrator)
            return "нет прав администратора";

        if (!string.IsNullOrWhiteSpace(disk.TelemetryNote))
            return disk.TelemetryNote;

        return "накопитель не вернул блок атрибутов. Чаще всего это RAID или " +
               "виртуальный диск: контроллер не передаёт S.M.A.R.T. операционной системе";
    }

    private static string Trim(string value, int length)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= length ? value : value.Substring(0, length - 1) + "…";
    }
}
