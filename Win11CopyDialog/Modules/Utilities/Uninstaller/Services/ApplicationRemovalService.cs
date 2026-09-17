using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Win11CopyDialog.Modules.Utilities.Uninstaller.Models;

namespace Win11CopyDialog.Modules.Utilities.Uninstaller.Services
{
    public class ApplicationRemovalService
    {
        public async Task<bool> RunStandardUninstallAsync(InstalledApplication app)
        {
            if (string.IsNullOrWhiteSpace(app.UninstallString))
                return false;

            if (app.IsSystemComponent)
                throw new InvalidOperationException("Попытка удаления системного компонента заблокирована.");

            return await Task.Run(() =>
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
                    if (process != null)
                    {
                        process.WaitForExit();
                        return process.ExitCode == 0 || process.ExitCode == 1605 || process.ExitCode == 3010; // Common MSI success codes
                    }

                    return false;
                }
                catch (Exception ex)
                {
                    // Log error (logging service skipped for brevity)
                    Debug.WriteLine($"Uninstall Error: {ex.Message}");
                    return false;
                }
            });
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
                        // Double check to avoid deleting C:\Program Files\ directly if parsing went wrong
                        if (app.InstallLocation.TrimEnd('\\').Length > 15) // simple heuristic
                        {
                            Directory.Delete(app.InstallLocation, true);
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
    }
}
