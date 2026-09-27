using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Win11CopyDialog.Modules.StorageControlCenter.Services;

/// <summary>
/// Сервис для работы с Volume Shadow Copy (VSS).
/// Позволяет создавать "горячие" теневые копии заблокированного системного диска (C:),
/// чтобы безопасно копировать реестр, базы данных и файлы ОС без перезагрузки в WinPE.
/// </summary>
public static class VssProviderService
{
    /// <summary>
    /// Максимальное время ожидания завершения mklink.
    /// Создание теневой копии реально медленное (диск + служба VSS), поэтому
    /// 30 с мало; но и «бесконечно» быть не может: раньше был WaitForExit()
    /// без таймаута, который намертво занимал поток пула, и кнопка «Отмена»
    /// миграции не могла ничего прервать (ct был только токеном СТАРТА Task.Run).
    /// </summary>
    private const int MkLinkTimeoutMs = 60_000;

    /// <summary>
    /// Создает теневую копию указанного диска (например, "C:\") и монтирует её в указанную папку (симлинк).
    /// Возвращает ID теневой копии для последующего удаления.
    /// </summary>
    public static async Task<(bool success, string shadowId, string message)> CreateAndMountShadowCopyAsync(string sourceDrive, string mountPointPath, CancellationToken ct = default)
    {
        return await Task.Run(async () =>
        {
            Process? runningProc = null;
            string shadowId = string.Empty;

            try
            {
                // Токен — это ещё и токен отмены СТАРТА, поэтому проверяем его
                // и внутри лямбды: иначе кнопка «Отмена» не влияет на работу.
                ct.ThrowIfCancellationRequested();

                // Убеждаемся, что sourceDrive в формате "C:\"
                if (!sourceDrive.EndsWith("\\"))
                    sourceDrive += "\\";

                // 1. Создаем теневую копию через WMI
                var classInstance = new ManagementClass("root\\CIMV2", "Win32_ShadowCopy", null);
                var methodParams = classInstance.GetMethodParameters("Create");
                methodParams["Context"] = "ClientAccessible";
                methodParams["Volume"] = sourceDrive;

                var outParams = classInstance.InvokeMethod("Create", methodParams, null);
                uint returnValue = (uint)(outParams?["ReturnValue"] ?? 999u);
                
                if (returnValue != 0)
                {
                    return (false, string.Empty, $"Ошибка создания VSS (Код: {returnValue})");
                }

                shadowId = outParams?["ShadowID"]?.ToString() ?? "";

                // 2. Получаем DeviceObject теневой копии
                string deviceObject = string.Empty;
                var searcher = new ManagementObjectSearcher($"SELECT * FROM Win32_ShadowCopy WHERE ID = '{shadowId}'");
                foreach (ManagementObject queryObj in searcher.Get())
                {
                    ct.ThrowIfCancellationRequested();
                    deviceObject = queryObj["DeviceObject"]?.ToString() ?? "";
                }

                if (string.IsNullOrEmpty(deviceObject))
                {
                    return (false, shadowId, "Теневая копия создана, но DeviceObject не найден.");
                }

                // ВАЖНО: Устройство VSS заканчивается без слеша, а для mklink нужен слеш
                string vssPath = deviceObject + "\\";

                // 3. Монтируем теневую копию через симлинк (mklink /D)
                if (Directory.Exists(mountPointPath))
                {
                    Directory.Delete(mountPointPath, false); // удаляем если это пустая папка или старый симлинк
                }

                var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /D \"{mountPointPath}\" \"{vssPath}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var proc = Process.Start(psi);
                runningProc = proc;
                if (proc is null)
                {
                    return (false, shadowId, "Не удалось запустить процесс mklink.");
                }

                // Чтение потоков запускаем СРАЗУ, но без токена: это единственный
                // способ не получить классический дедлок, когда ребёнок забивает
                // буфер pipe и не может завершиться, пока мы не прочитаем данные.
                // Ожидание выхода — только здесь, и только с таймаутом + отменой.
                Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = proc.StandardError.ReadToEndAsync();

                using var timeoutCts = new CancellationTokenSource(MkLinkTimeoutMs);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                try
                {
                    // ВАЖНО: сначала дожидаемся выхода процесса, ПОТОМ читаем вывод.
                    //
                    // Ловушка StreamReader.ReadToEndAsync(ct): токен проверяется
                    // только в момент старта чтения. Если чтение уже началось,
                    // отмена его НЕ прерывает — ReadToEndAsync(ct) висит до тех
                    // пор, пока писатель не закроет канал. Зависший процесс,
                    // который не пишет и не выходит, держал бы канал открытым
                    // вечно, и никакой отмены это не спасло бы. Поэтому читать
                    // вывод с токеном «на спасение» бесполезно: сначала выход,
                    // потом чтение уже мёртвого процесса, которое завершается.
                    await proc.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Таймаут или отмена — процесс мог зависнуть. Убиваем ДЕРЕВО
                    // (cmd.exe мог породить потомков) и сообщаем об ошибке.
                    KillProcessTree(proc);

                    // Вывод больше не читаем: ждать его после kill — как раз тот
                    // случай, где чтение может не вернуться. Просто гасим
                    // возможные неотслеженные исключения фоновых чтений.
                    ForgetOutput(stdoutTask);
                    ForgetOutput(stderrTask);

                    if (ct.IsCancellationRequested)
                    {
                        return (false, shadowId, "Создание теневой копии отменено пользователем.");
                    }

                    return (false, shadowId,
                        $"Таймаут создания симлинка (mklink): процесс не завершился за {MkLinkTimeoutMs / 1000} сек и был принудительно завершён.");
                }

                // Процесс завершён — пайпы закрыты, чтение до конца гарантированно
                // возвращается. Ожидаем без .Result и без бесконечного блокирования.
                string stdout = await stdoutTask.ConfigureAwait(false);
                string stderr = await stderrTask.ConfigureAwait(false);

                if (proc.ExitCode != 0)
                {
                    string detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                    return (false, shadowId, $"Ошибка создания симлинка (mklink), код {proc.ExitCode}: {detail.Trim()}");
                }

                return (true, shadowId, "Теневая копия успешно создана и смонтирована.");
            }
            catch (OperationCanceledException)
            {
                // Отмену не маскируем под «исключение VSS»: вызывающий код
                // (MigrationOrchestratorService) ловит OperationCanceledException
                // отдельно и показывает пользователю «миграция отменена».
                if (runningProc is not null)
                {
                    KillProcessTree(runningProc);
                }

                throw;
            }
            catch (Exception ex)
            {
                // Уже завершённый/подвисший процесс не должен «утопить» результат.
                if (runningProc is not null)
                {
                    KillProcessTree(runningProc);
                }

                return (false, string.Empty, $"Исключение при работе с VSS: {ex.Message}");
            }
        }, ct);
    }

    /// <summary>
    /// Принудительно завершает процесс и всех его потомков. Все ошибки глотаются:
    /// вызывается в аварийных ветках, где уже формируется результат операции.
    /// </summary>
    private static void KillProcessTree(Process proc)
    {
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Процесс уже мёртв, либо его нельзя завершить — не критично.
        }
    }

    /// <summary>
    /// Помечает фоновую задачу чтения как «больше не интересна»: её результат
    /// не нужен, но возможное исключение не должно уйти в неотслеженные.
    /// </summary>
    private static void ForgetOutput(Task<string> readTask)
    {
        _ = readTask.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Удаляет теневую копию по её ID и очищает точку монтирования (симлинк).
    /// </summary>
    public static void CleanupShadowCopy(string shadowId, string mountPointPath)
    {
        // 1. Удаляем симлинк
        if (Directory.Exists(mountPointPath))
        {
            try
            {
                // Симлинки удаляются через стандартный Directory.Delete (он не трогает целевые файлы VSS)
                Directory.Delete(mountPointPath, false);
            }
            catch { }
        }

        // 2. Удаляем саму теневую копию через WMI
        if (!string.IsNullOrEmpty(shadowId))
        {
            try
            {
                var searcher = new ManagementObjectSearcher($"SELECT * FROM Win32_ShadowCopy WHERE ID = '{shadowId}'");
                foreach (ManagementObject queryObj in searcher.Get())
                {
                    queryObj.Delete();
                }
            }
            catch { }
        }
    }
}
