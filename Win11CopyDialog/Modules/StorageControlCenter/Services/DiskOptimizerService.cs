using System.Diagnostics;
using Win11CopyDialog.Modules.StorageControlCenter.Models;

namespace Win11CopyDialog.Modules.StorageControlCenter.Services;

public static class DiskOptimizerService
{
    /// <summary>
    /// Максимальное время анализа фрагментации через `defrag /A`.
    /// Анализ ��алочен и запускается синхронно при открытии вкладки
    /// «Накопители», поэтому он обязан быть ограничен по времени.
    /// </summary>
    private static readonly TimeSpan FragmentationAnalysisTimeout = TimeSpan.FromSeconds(8);

    public static async Task<(bool success, string output)> OptimizeDriveAsync(
        StorageDisk disk,
        string driveLetter,
        string mode = "Smart",
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        string letter = driveLetter.TrimEnd('\\', ':');
        progress?.Report($"Инициализация оптимизации накопителя {letter}:...");

        if (disk.MediaType == StoragePhysicalMedia.NVMeSSD || disk.MediaType == StoragePhysicalMedia.SataSSD)
        {
            // SSD / NVMe -> Выполняем ReTrim
            progress?.Report($"Обнаружен твердотельный накопитель ({disk.MediaTypeString}). Традиционная дефрагментация отключена для защиты ячеек памяти.");
            progress?.Report($"Отправка команд TRIM (ReTrim) на контроллер накопителя...");

            string script = $"Optimize-Volume -DriveLetter '{letter}' -ReTrim -Verbose";
            var res = await RunPowerShellScriptAsync(script, ct);

            if (res.exitCode == 0)
            {
                progress?.Report("✔ Команды TRIM успешно обработаны контроллером. Свободные блоки памяти очищены.");
                return (true, "TRIM оптимизация успешно завершена.");
            }

            // Раньше здесь возвращался success=true при ненулевом exit-коде, из-за чего
            // UI рапортовал об успехе после реально упавшего Optimize-Volume.
            progress?.Report($"⚠ TRIM не выполнен (код {res.exitCode}). Требуются повышенные привилегии администратора.");
            return (false, Summarize(res.output));
        }
        else if (disk.MediaType == StoragePhysicalMedia.HDD)
        {
            // HDD -> Выполняем дефрагментацию
            progress?.Report($"Обнаружен магнитный накопитель ({disk.MediaTypeString}). Запуск дефрагментации дорожек и файлов ({mode})...");

            string flag = mode switch
            {
                "Deep" => "/X",
                "Quick" => "/U",
                _ => "/U /V"
            };

            var res = await RunProcessAsync("defrag.exe", new[] { $"{letter}:", flag }, ct);
            if (res.exitCode == 0)
            {
                progress?.Report("✔ Дефрагментация жесткого диска успешно завершена. Фрагменты файлов объединены.");
                return (true, res.output);
            }

            // Раньше здесь возвращался success=true при ненулевом коде — UI показывал
            // «✔ Оптимизация завершена» после реально упавшей операции.
            progress?.Report($"⚠ Дефрагментация завершилась с кодом {res.exitCode}: {Summarize(res.output)}");
            return (false, Summarize(res.output));
        }
        else
        {
            // Раньше эта ветка вообще ничего не проверяла: ждала 500 мс и рапортовала
            // «✔ Файловая система проверена». Теперь выполняется реальная проверка.
            progress?.Report("Проверка целостности файловой системы накопителя...");
            var res = await RunProcessAsync("chkdsk.exe", new[] { $"{letter}:", "/scan" }, ct);

            if (res.exitCode == 0)
            {
                progress?.Report("✔ Проверка файловой системы завершена без ошибок. Накопитель готов к работе.");
                return (true, res.output);
            }

            progress?.Report($"⚠ Проверка завершилась с кодом {res.exitCode}: {Summarize(res.output)}");
            return (false, Summarize(res.output));
        }
    }

    /// <summary>Сжимает многострочный вывод утилиты до одной строки для показа в UI.</summary>
    private static string Summarize(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return "(нет вывода)";

        var lines = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0);

        var text = string.Join(" | ", lines);
        return text.Length > 400 ? text[..400] + "..." : text;
    }

    /// <summary>
    /// Возвращает процент фрагментации, либо null, если определить не удалось.
    /// Раньше здесь был catch { } + return 0.0, из-за чего ошибка выглядела
    /// как «0% фрагментации» — то есть как идеально дефрагментированный том.
    /// </summary>
    public static async Task<double?> AnalyzeFragmentationAsync(string driveLetter, CancellationToken ct = default)
    {
        string letter = driveLetter.TrimEnd('\\', ':');
        try
        {
            // ЖЁСТКИЙ ТАЙМАУТ.
            // `defrag /A` сканирует весь том и на большом HDD может идти минутами.
            // Раньше здесь передавался CancellationToken.None, поэтому
            // StorageDiscoveryService — который вызывает этот метод СИНХРОННО
            // через GetAwaiter().GetResult() при открытии вкладки «Накопители» —
            // висел намертво до самого конца анализа.
            // Показать честное «Нет данных» лучше, чем повесить программу.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(FragmentationAnalysisTimeout);
            var cts = timeout.Token;

            Debug($"запускаю defrag {letter}: /A, таймаут {FragmentationAnalysisTimeout.TotalSeconds}с");
            var res = await RunProcessAsync("defrag.exe", new[] { $"{letter}:", "/A" }, cts);
            Debug($"defrag завершился, код {res.exitCode}, вывод {res.output.Length} симв.");

            if (res.exitCode != 0)
            {
                // Утилита не сработала — не выдумываем значение.
                return null;
            }

            var lines = res.output.Split('\n');
            foreach (var line in lines)
            {
                if (!line.Contains('%')) continue;

                int pIdx = line.IndexOf('%');
                if (pIdx < 0) continue;

                int sIdx = Math.Max(0, pIdx - 4);
                string sub = line.Substring(sIdx, pIdx - sIdx).Trim('=', ' ', ':');

                if (double.TryParse(sub,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out double pct))
                {
                    return pct;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw; // Отмена — не ошибка, пробросить вызывающему.
        }
        catch (Exception)
        {
            // Отказоустойчиво: лучше «неизвестно», чем ложное «0%».
            return null;
        }

        return null;
    }

    private static async Task<(int exitCode, string output)> RunPowerShellScriptAsync(string script, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        // ArgumentList вместо строковой интерполяции: защита от инъекции в -Command.
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);

        return await RunProcessCoreAsync(psi, ct);
    }

    private static async Task<(int exitCode, string output)> RunProcessAsync(string exe, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var a in args) psi.ArgumentList.Add(a);

        return await RunProcessCoreAsync(psi, ct);
    }

    /// <summary>
    /// Запускает процесс и читает stdout/stderr ОДНОВРЕМЕННО.
    ///
    /// Важно: обе задачи чтения стартуют до ожидания. Последовательное
    /// «await stdout, затем await stderr» приводит к классическому deadlock —
    /// дочерний процесс заполняет буфер stderr (4 КБ), блокируется, и никогда
    /// не пишет EOF в stdout, поэтому родитель ждёт бесконечно.
    /// </summary>
    /// <summary>
    /// Запуск утилиты с чтением stdout/stderr и корректной отменой.
    ///
    /// <para>Почему нельзя просто передавать токен в ReadToEndAsync:
    /// StreamReader.ReadToEndAsync(CancellationToken) проверяет токен
    /// ТОЛЬКО в начале операции. Если чтение уже началось и процесс молчит,
    /// отмена не срабатывает никогда — задача висит до бесконечности, а
    /// таймаут оказывается бесполезным. Именно это и подвешивало вкладку
    /// «Накопители»: `defrag /A` на большом HDD не отдаёт вывод, пока
    /// сканирует том.</para>
    ///
    /// <para>Корректная схема: ждём завершения процесса (это отменяемо),
    /// и только потом читаем уже закрытые потоки. По таймауту процесс
    /// убивается, что закрывает каналы и завершает чтения.</para>
    /// </summary>
    private static async Task<(int exitCode, string output)> RunProcessCoreAsync(ProcessStartInfo psi, CancellationToken ct)
    {
        using var proc = new Process { StartInfo = psi };
        proc.Start();

        // Читаем БЕЗ токена: отменять эти чтения всё равно нечем, зато
        // они гарантированно завершатся, когда процесс закроет каналы.
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Debug($"WaitForExit отменён по таймауту, убиваю {psi.FileName}");
            // Убиваем процесс: иначе defrag продолжит сканировать диск в фоне
            // после того, как пользователь закрыл окно.
            TryKill(proc);
            throw;
        }

        // Процесс завершился — каналы закрыты, чтения вот-вот завершатся.
        // Ждём их с пределом: внук может унаследовать дескриптор и держать
        // канал открытым, и тогда задача не завершится никогда.
        var reads = Task.WhenAll(stdoutTask, stderrTask);
        var finished = await Task.WhenAny(reads, Task.Delay(DrainTimeout, ct)).ConfigureAwait(false);
        if (finished != reads) return (-1, "");

        return (proc.ExitCode, stdoutTask.Result + "\n" + stderrTask.Result);
    }

    /// <summary>Сколько ждать, пока каналы процессов освободятся после его завершения.</summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Диагностический вывод из запуска внешних процессов.</summary>
    internal static bool ProcessTraceEnabled
    {
        get => StorageDiscoveryService.TraceEnabled;
        set => StorageDiscoveryService.TraceEnabled = value;
    }

    private static void Debug(string msg)
    {
        if (!ProcessTraceEnabled) return;
        System.Console.WriteLine($"      [defrag-диагностика] {msg}");
        System.Console.Out.Flush();
    }

    private static void TryKill(Process proc)
    {
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                // Даём процессу короткое время на завершение, иначе Dispose
                // бросит InvalidOperationException на уже мёртвый процесс.
                proc.WaitForExit(2000);
            }
        }
        catch
        {
            // Процесс мог завершиться сам между проверкой и убийством.
        }
    }
}
