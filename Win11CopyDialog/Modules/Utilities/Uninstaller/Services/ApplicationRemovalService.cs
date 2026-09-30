using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Win11CopyDialog.Modules.Utilities.Uninstaller.Models;

namespace Win11CopyDialog.Modules.Utilities.Uninstaller.Services
{
    public class ApplicationRemovalService
    {
        /// <summary>
        /// Максимальное время ожидания завершения деинсталлятора.
        /// MSI сам по себе медленный, но он также может запросить перезагрузку
        /// или показать модальное окно UAC и ПРОЖИВАТЬ вечно. Раньше здесь стоял
        /// WaitForExit() без таймаута, из-за чего Uninstall_Click висел в await
        /// бесконечно, а поток пула был занят навсегда.
        /// </summary>
        private const int UninstallTimeoutMs = 120_000;

        public async Task<bool> RunStandardUninstallAsync(InstalledApplication app, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(app.UninstallString))
                return false;

            if (app.IsSystemComponent)
                throw new InvalidOperationException("Попытка удаления системного компонента заблокирована.");

            return await Task.Run(async () =>
            {
                try
                {
                    // Split executable and arguments
                    string executable = app.UninstallString;
                    string arguments = "";

                    if (executable.StartsWith("\""))
                    {
                        int endQuote = executable.IndexOf("\"", 1);
                        if (endQuote > 0)
                        {
                            arguments = executable.Substring(endQuote + 1).Trim();
                            executable = executable.Substring(1, endQuote - 1);
                        }
                    }
                    else
                    {
                        int firstSpace = executable.IndexOf(" ");
                        if (firstSpace > 0)
                        {
                            arguments = executable.Substring(firstSpace + 1).Trim();
                            executable = executable.Substring(0, firstSpace);
                        }
                    }

                    var psi = new ProcessStartInfo
                    {
                        FileName = executable,
                        Arguments = arguments,
                        UseShellExecute = true,
                        Verb = "runas" // Request elevation
                    };

                    using var process = Process.Start(psi);
                    if (process is null)
                    {
                        return false;
                    }

                    // Токен — не только токен старта Task.Run: проверяем его и здесь,
                    // иначе отмена операции не влияет на уже запущенный процесс.
                    if (ct.IsCancellationRequested)
                    {
                        KillProcessTree(process);
                        return false;
                    }

                    using var timeoutCts = new CancellationTokenSource(UninstallTimeoutMs);
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                    try
                    {
                        // Сначала дожидаемся ВЫХОДА процесса, и только потом
                        // читаем его результат (ExitCode/вывод).
                        //
                        // Ловушка StreamReader.ReadToEndAsync(ct): токен
                        // проверяется лишь в момент старта чтения. Начатое
                        // чтение отмена НЕ прерывает — оно висит, пока писатель
                        // не закроет канал. Зависший деинсталлятор, который не
                        // пишет и не выходит, держал бы канал открытым вечно,
                        // и никакой отмены это бы не спасло. Поэтому «читать с
                        // токеном на спасение» бесполезно: сначала гарантированный
                        // выход, затем чтение уже мёртвого процесса.
                        //
                        // Здесь вывод и не читается вовсе: UseShellExecute = true
                        // (иначе elevation через Verb = "runas" не работает)
                        // запрещает перенаправление потоков.
                        await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Таймаут или отмена: деинсталлятор завис (или просит
                        // перезагрузку и ждёт её). Убиваем всё дерево процессов.
                        KillProcessTree(process);

                        if (ct.IsCancellationRequested)
                        {
                            Debug.WriteLine($"Uninstall cancelled: {app.DisplayName}");
                            return false;
                        }

                        Debug.WriteLine($"Uninstall timed out after {UninstallTimeoutMs} ms: {app.DisplayName}");
                        return false;
                    }

                    // ExitCode читается ТОЛЬКО после успешного ограниченного ожидания.
                    return process.ExitCode == 0 || process.ExitCode == 1605 || process.ExitCode == 3010; // Common MSI success codes
                }
                catch (Exception ex)
                {
                    // Log error (logging service skipped for brevity)
                    Debug.WriteLine($"Uninstall Error: {ex.Message}");
                    return false;
                }
            }, ct);
        }

        public async Task<bool> RunForceUninstallAsync(InstalledApplication app, bool dryRun)
        {
            if (app.IsSystemComponent)
                throw new InvalidOperationException("Принудительное удаление системных компонентов строго запрещено.");

            return await Task.Run(() =>
            {
                // Safety Principle: In Force Uninstall, we don't automatically delete files 
                // just by name. We remove only explicitly matched InstallLocation and exact RegistryKeyPath.
                
                if (dryRun)
                {
                    // Simulate the plan
                    Debug.WriteLine($"[DRY RUN] Would delete registry key: {app.RegistryKeyPath}");
                    if (!string.IsNullOrWhiteSpace(app.InstallLocation) && Directory.Exists(app.InstallLocation))
                    {
                        Debug.WriteLine($"[DRY RUN] Would delete directory: {app.InstallLocation}");
                    }
                    return true;
                }

                bool success = true;

                // 1. Delete Registry Key
                if (!string.IsNullOrWhiteSpace(app.RegistryKeyPath))
                {
                    try
                    {
                        // Example path: HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AppName
                        string[] parts = app.RegistryKeyPath.Split(new[] { '\\' }, 2);
                        if (parts.Length == 2)
                        {
                            Microsoft.Win32.RegistryKey baseKey = parts[0] == "HKEY_LOCAL_MACHINE" ? Microsoft.Win32.Registry.LocalMachine : Microsoft.Win32.Registry.CurrentUser;
                            baseKey.DeleteSubKeyTree(parts[1], throwOnMissingSubKey: false);
                        }
                    }
                    catch
                    {
                        success = false;
                    }
                }

                // 2. Delete Install Directory
                if (!string.IsNullOrWhiteSpace(app.InstallLocation) && Directory.Exists(app.InstallLocation))
                {
                    try
                    {
                        // Строгая проверка безопасности пути перед удалением
                        if (IsSafeToDeleteDirectory(app.InstallLocation))
                        {
                            Directory.Delete(app.InstallLocation, true);
                        }
                        else
                        {
                            Debug.WriteLine($"[SECURITY] Отклонена попытка удаления защищённого каталога: {app.InstallLocation}");
                            success = false;
                        }
                    }
                    catch
                    {
                        success = false;
                    }
                }

                return success;
            });
        }

        /// <summary>
        /// Принудительно завершает процесс и всех его потомков (деинсталлятор
        /// часто запускает msiexec/helper-процессы). Ошибки глотаются: метод
        /// вызывается в ветках, где уже формируется итоговый результат операции.
        /// </summary>
        private static void KillProcessTree(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Процесс уже завершён или недоступен — не критично.
            }
        }

        private static bool IsSafeToDeleteDirectory(string dirPath)
        {
            if (string.IsNullOrWhiteSpace(dirPath)) return false;
            try
            {
                string full = Path.GetFullPath(dirPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string root = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? "";

                if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return false;

                var prohibited = new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                };

                foreach (var p in prohibited)
                {
                    if (string.IsNullOrWhiteSpace(p)) continue;
                    string normP = Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (string.Equals(full, normP, StringComparison.OrdinalIgnoreCase)) return false;
                }

                string? parent = Directory.GetParent(full)?.FullName;
                if (parent == null || string.Equals(parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase))
                {
                    string name = Path.GetFileName(full);
                    if (name.Equals("Windows", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Program Files", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Program Files (x86)", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Users", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Recovery", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
