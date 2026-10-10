using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Win11CopyDialog.Modules.Utilities.Uninstaller.Models;

namespace Win11CopyDialog.Modules.Utilities.Uninstaller.Services
{
    public class ResidualItem
    {
        public string PathOrKey { get; set; } = string.Empty;
        public bool IsRegistry { get; set; }
        public long SizeBytes { get; set; }
        public string SizeFormatted { get; set; } = string.Empty;
        public bool IsSelected { get; set; } = true;
    }

    public class ApplicationRemovalService
    {
        private const int UninstallTimeoutMs = 180_000;

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
                    var (executable, arguments) = SplitCommandLine(app.UninstallString);
                    return await ExecuteUninstallProcessAsync(executable, arguments, ct);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Standard Uninstall Error: {ex.Message}");
                    return false;
                }
            }, ct);
        }

        public async Task<bool> RunQuietUninstallAsync(InstalledApplication app, CancellationToken ct = default)
        {
            if (app.IsSystemComponent)
                throw new InvalidOperationException("Попытка тихого удаления системного компонента заблокирована.");

            return await Task.Run(async () =>
            {
                try
                {
                    string commandToRun = string.Empty;

                    if (!string.IsNullOrWhiteSpace(app.QuietUninstallString))
                    {
                        commandToRun = app.QuietUninstallString;
                    }
                    else if (app.IsMsi || app.UninstallString.IndexOf("msiexec", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        // MSI quiet uninstall
                        string productCode = ExtractProductCode(app.UninstallString);
                        if (!string.IsNullOrEmpty(productCode))
                        {
                            commandToRun = $"msiexec.exe /x {productCode} /qn /norestart";
                        }
                    }
                    else if (app.UninstallString.IndexOf("unins", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        // Inno Setup
                        var (exe, args) = SplitCommandLine(app.UninstallString);
                        commandToRun = $"\"{exe}\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART {args}";
                    }
                    else if (app.UninstallString.IndexOf("uninstall.exe", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        // NSIS
                        var (exe, args) = SplitCommandLine(app.UninstallString);
                        commandToRun = $"\"{exe}\" /S {args}";
                    }

                    if (string.IsNullOrEmpty(commandToRun))
                    {
                        // Fallback to standard command
                        commandToRun = app.UninstallString;
                    }

                    var (executable, arguments) = SplitCommandLine(commandToRun);
                    return await ExecuteUninstallProcessAsync(executable, arguments, ct);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Quiet Uninstall Error: {ex.Message}");
                    return false;
                }
            }, ct);
        }

        private async Task<bool> ExecuteUninstallProcessAsync(string executable, string arguments, CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas"
            };

            using var process = Process.Start(psi);
            if (process == null) return false;

            if (ct.IsCancellationRequested)
            {
                KillProcessTree(process);
                return false;
            }

            using var timeoutCts = new CancellationTokenSource(UninstallTimeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                KillProcessTree(process);
                return false;
            }

            return process.ExitCode == 0 || process.ExitCode == 1605 || process.ExitCode == 3010;
        }

        public async Task<bool> RunForceUninstallAsync(InstalledApplication app, bool dryRun)
        {
            if (app.IsSystemComponent)
                throw new InvalidOperationException("Принудительное удаление системных компонентов строго запрещено.");

            return await Task.Run(() =>
            {
                if (dryRun) return true;

                bool success = true;

                // 1. Delete Registry Key
                if (!string.IsNullOrWhiteSpace(app.RegistryKeyPath))
                {
                    try
                    {
                        string[] parts = app.RegistryKeyPath.Split(new[] { '\\' }, 2);
                        if (parts.Length == 2)
                        {
                            var baseKey = parts[0] == "HKEY_LOCAL_MACHINE" || parts[0] == "LocalMachine"
                                ? Registry.LocalMachine
                                : Registry.CurrentUser;
                            baseKey.DeleteSubKeyTree(parts[1], throwOnMissingSubKey: false);
                        }
                    }
                    catch
                    {
                        success = false;
                    }
                }

                // 2. Delete Install Directory safely
                if (!string.IsNullOrWhiteSpace(app.InstallLocation) && Directory.Exists(app.InstallLocation))
                {
                    try
                    {
                        if (IsSafeToDeleteDirectory(app.InstallLocation))
                        {
                            Directory.Delete(app.InstallLocation, true);
                        }
                        else
                        {
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

        public async Task<List<ResidualItem>> ScanResidualsAsync(InstalledApplication app)
        {
            return await Task.Run(() =>
            {
                var list = new List<ResidualItem>();
                if (string.IsNullOrWhiteSpace(app.DisplayName)) return list;

                string cleanName = SanitizeForSearch(app.DisplayName);
                string cleanPublisher = SanitizeForSearch(app.Publisher);

                // Check standard folder locations
                var searchDirs = new List<string>
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), // ProgramData
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow")
                };

                foreach (var baseDir in searchDirs)
                {
                    if (!Directory.Exists(baseDir)) continue;

                    // Match app name
                    try
                    {
                        var matching = Directory.EnumerateDirectories(baseDir, $"*{cleanName}*", SearchOption.TopDirectoryOnly);
                        foreach (var match in matching)
                        {
                            if (IsSafeToDeleteDirectory(match))
                            {
                                long size = CalculateDirectorySize(match);
                                list.Add(new ResidualItem
                                {
                                    PathOrKey = match,
                                    IsRegistry = false,
                                    SizeBytes = size,
                                    SizeFormatted = ApplicationDiscoveryService.FormatBytes(size)
                                });
                            }
                        }
                    }
                    catch { }
                }

                // Check registry leftovers: HKCU\Software\Name and HKLM\Software\Name
                CheckRegistryResidual(Registry.CurrentUser, @"Software", cleanName, list);
                CheckRegistryResidual(Registry.LocalMachine, @"Software", cleanName, list);

                return list;
            });
        }

        public async Task<int> CleanResidualsAsync(IEnumerable<ResidualItem> items)
        {
            return await Task.Run(() =>
            {
                int count = 0;
                foreach (var item in items.Where(x => x.IsSelected))
                {
                    try
                    {
                        if (item.IsRegistry)
                        {
                            string[] parts = item.PathOrKey.Split(new[] { '\\' }, 2);
                            if (parts.Length == 2)
                            {
                                var baseKey = parts[0].Contains("LOCAL_MACHINE") ? Registry.LocalMachine : Registry.CurrentUser;
                                baseKey.DeleteSubKeyTree(parts[1], throwOnMissingSubKey: false);
                                count++;
                            }
                        }
                        else
                        {
                            if (Directory.Exists(item.PathOrKey) && IsSafeToDeleteDirectory(item.PathOrKey))
                            {
                                Directory.Delete(item.PathOrKey, true);
                                count++;
                            }
                            else if (File.Exists(item.PathOrKey))
                            {
                                File.Delete(item.PathOrKey);
                                count++;
                            }
                        }
                    }
                    catch { }
                }
                return count;
            });
        }

        private static void CheckRegistryResidual(RegistryKey root, string sub, string name, List<ResidualItem> list)
        {
            try
            {
                using var key = root.OpenSubKey(sub);
                if (key == null) return;
                foreach (var subName in key.GetSubKeyNames())
                {
                    if (subName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        list.Add(new ResidualItem
                        {
                            PathOrKey = $@"{root.Name}\{sub}\{subName}",
                            IsRegistry = true,
                            SizeBytes = 0,
                            SizeFormatted = "Ключ реестра"
                        });
                    }
                }
            }
            catch { }
        }

        private static string SanitizeForSearch(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;
            string clean = input.Split(new[] { ' ', '(', ')', '-', '_' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? input;
            return clean.Trim();
        }

        private static long CalculateDirectorySize(string path)
        {
            try
            {
                var di = new DirectoryInfo(path);
                return di.EnumerateFiles("*", SearchOption.AllDirectories).Sum(fi => fi.Length);
            }
            catch { return 0; }
        }

        public static void OpenInstallLocation(InstalledApplication app)
        {
            if (app.InstallLocationExists)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{app.InstallLocation}\"",
                    UseShellExecute = true
                });
            }
        }

        public static void OpenInRegistry(InstalledApplication app)
        {
            if (string.IsNullOrWhiteSpace(app.RegistryKeyPath)) return;

            try
            {
                // Write LastKey to Regedit
                using (var regKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit"))
                {
                    regKey?.SetValue("LastKey", app.RegistryKeyPath);
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = "regedit.exe",
                    UseShellExecute = true
                });
            }
            catch { }
        }

        public static void SearchOnline(InstalledApplication app)
        {
            string query = Uri.EscapeDataString($"{app.DisplayName} {app.Publisher}".Trim());
            string url = $"https://www.google.com/search?q={query}";
            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch { }
        }

        private static (string executable, string arguments) SplitCommandLine(string commandLine)
        {
            string executable = commandLine.Trim();
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

            return (executable, arguments);
        }

        private static string ExtractProductCode(string text)
        {
            int openBrace = text.IndexOf('{');
            int closeBrace = text.IndexOf('}');
            if (openBrace >= 0 && closeBrace > openBrace)
            {
                return text.Substring(openBrace, closeBrace - openBrace + 1);
            }
            return string.Empty;
        }

        private static bool IsSafeToDeleteDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            try
            {
                string fullPath = Path.GetFullPath(path).TrimEnd('\\');
                string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
                string system32 = Environment.GetFolderPath(Environment.SpecialFolder.System).TrimEnd('\\');
                string progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles).TrimEnd('\\');
                string progFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86).TrimEnd('\\');
                string root = Path.GetPathRoot(fullPath)?.TrimEnd('\\') ?? "";

                if (fullPath.Equals(root, StringComparison.OrdinalIgnoreCase)) return false;
                if (fullPath.Equals(windir, StringComparison.OrdinalIgnoreCase)) return false;
                if (fullPath.Equals(system32, StringComparison.OrdinalIgnoreCase)) return false;
                if (fullPath.Equals(progFiles, StringComparison.OrdinalIgnoreCase)) return false;
                if (fullPath.Equals(progFilesX86, StringComparison.OrdinalIgnoreCase)) return false;
                if (fullPath.StartsWith(system32, StringComparison.OrdinalIgnoreCase)) return false;

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void KillProcessTree(Process parentProcess)
        {
            try
            {
                int pid = parentProcess.Id;
                using var searcher = new ManagementObjectSearcher(
                    $"Select ProcessId From Win32_Process Where ParentProcessId={pid}");
                using var collection = searcher.Get();

                foreach (var item in collection)
                {
                    try
                    {
                        int childPid = Convert.ToInt32(item["ProcessId"]);
                        using var child = Process.GetProcessById(childPid);
                        KillProcessTree(child);
                    }
                    catch { }
                }

                if (!parentProcess.HasExited)
                {
                    parentProcess.Kill(entireProcessTree: true);
                }
            }
            catch { }
        }
    }
}
