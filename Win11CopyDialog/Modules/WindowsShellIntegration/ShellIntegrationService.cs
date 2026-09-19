using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Win11CopyDialog.Modules.WindowsShellIntegration;

/// <summary>
/// Сервис системной интеграции с Windows Shell и замены стандартного Проводника (Windows Explorer).
/// Выполняет безопасную запись в ветку пользователя HKCU (HKEY_CURRENT_USER\Software\Classes),
/// не повреждая системный explorer.exe и позволяя мгновенно переключаться туда и обратно.
/// </summary>
public static class ShellIntegrationService
{
    private const string AppTitle = "Motion Commander";

    [DllImport("shell32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    private const uint SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;

    /// <summary>
    /// Оповестить Windows Shell об изменении системных файловых ассоциаций
    /// </summary>
    public static void NotifyShellAssociationsChanged()
    {
        try
        {
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }
    }

    // =========================================================================
    // 1. ЗАМЕНА СТАНДАРТНОГО ПРОВОДНИКА WINDOWS EXPLORER И ВОЗВРАТ ОБРАТНО
    // =========================================================================

    /// <summary>
    /// Проверяет, активен ли Motion Commander в качестве проводника по умолчанию
    /// </summary>
    public static bool IsExplorerReplaced()
    {
        try
        {
            using var folderKey = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Folder\shell\open\command");
            if (folderKey != null)
            {
                string? cmd = folderKey.GetValue("") as string;
                if (!string.IsNullOrEmpty(cmd) && (cmd.Contains("Win11CopyDialog", StringComparison.OrdinalIgnoreCase) || cmd.Contains("MotionCommander", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            using var dirKey = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Directory\shell\open\command");
            if (dirKey != null)
            {
                string? cmd = dirKey.GetValue("") as string;
                if (!string.IsNullOrEmpty(cmd) && (cmd.Contains("Win11CopyDialog", StringComparison.OrdinalIgnoreCase) || cmd.Contains("MotionCommander", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Устанавливает или отменяет замену стандартного Проводника Windows (Folder, Directory, Drive).
    /// </summary>
    /// <param name="replace">true — сделать Motion Commander проводником по умолчанию; false — вернуть стандартный Windows Explorer</param>
    /// <param name="error">Сообщение об ошибке, если операция не удалась</param>
    public static bool SetExplorerReplacement(bool replace, out string error)
    {
        error = "";
        try
        {
            string exePath = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName
                ?? Path.Combine(AppContext.BaseDirectory, "Win11CopyDialog.exe");

            if (replace)
            {
                string openCommand = $"\"{exePath}\" \"%1\"";

                // 1. HKCU\Software\Classes\Folder (папки, архивы, виртуальные контейнеры)
                using (var folderShell = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Folder\shell"))
                {
                    folderShell.SetValue("", "open");
                }
                using (var folderOpenCmd = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Folder\shell\open\command"))
                {
                    folderOpenCmd.SetValue("", openCommand);
                    folderOpenCmd.DeleteValue("DelegateExecute", false);
                }

                // 2. HKCU\Software\Classes\Directory (директории файловой системы)
                using (var dirShell = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\shell"))
                {
                    dirShell.SetValue("", "open");
                }
                using (var dirOpenCmd = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\shell\open\command"))
                {
                    dirOpenCmd.SetValue("", openCommand);
                    dirOpenCmd.DeleteValue("DelegateExecute", false);
                }

                // 3. HKCU\Software\Classes\Drive (логические диски и тома, C:\, D:\ и т.д.)
                using (var driveShell = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Drive\shell"))
                {
                    driveShell.SetValue("", "open");
                }
                using (var driveOpenCmd = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Drive\shell\open\command"))
                {
                    driveOpenCmd.SetValue("", openCommand);
                    driveOpenCmd.DeleteValue("DelegateExecute", false);
                }
            }
            else
            {
                // Возврат стандартного Проводника: удаляем ветки open в HKCU
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Folder\shell\open", false);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Directory\shell\open", false);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Drive\shell\open", false);

                // Сбрасываем дефолтное действие в shell, возвращая Windows к чтению HKLM
                using (var folderShell = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Folder\shell", true))
                {
                    folderShell?.DeleteValue("", false);
                }
                using (var dirShell = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Directory\shell", true))
                {
                    dirShell?.DeleteValue("", false);
                }
                using (var driveShell = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Drive\shell", true))
                {
                    driveShell?.DeleteValue("", false);
                }
            }

            NotifyShellAssociationsChanged();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // =========================================================================
    // 2. ИНТЕГРАЦИЯ В КОНТЕКСТНОЕ МЕНЮ (СЖАТЬ, ОТКРЫТЬ В MOTION COMMANDER)
    // =========================================================================

    public static bool IsIntegrated()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\*\shell\MotionCommander");
            return key != null;
        }
        catch
        {
            return false;
        }
    }

    public static bool SetIntegration(bool enable, out string error)
    {
        error = "";
        try
        {
            string exePath = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName
                ?? Path.Combine(AppContext.BaseDirectory, "Win11CopyDialog.exe");

            if (enable)
            {
                // 1. Для всех файлов: "Сжать в архив..."
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\*\shell\MotionCommander"))
                {
                    key.SetValue("", $"Сжать в архив ({AppTitle})...");
                    key.SetValue("Icon", $"\"{exePath}\",0");
                    using var cmd = key.CreateSubKey("command");
                    cmd.SetValue("", $"\"{exePath}\" --compress \"%1\"");
                }

                // 2. Для папок: "Открыть в Motion Commander"
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\shell\MotionCommander"))
                {
                    key.SetValue("", $"Открыть в {AppTitle}");
                    key.SetValue("Icon", $"\"{exePath}\",0");
                    using var cmd = key.CreateSubKey("command");
                    cmd.SetValue("", $"\"{exePath}\" \"%1\"");
                }

                // 3. Для фона папок
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\Background\shell\MotionCommander"))
                {
                    key.SetValue("", $"Открыть в {AppTitle}");
                    key.SetValue("Icon", $"\"{exePath}\",0");
                    using var cmd = key.CreateSubKey("command");
                    cmd.SetValue("", $"\"{exePath}\" \"%V\"");
                }
            }
            else
            {
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\*\shell\MotionCommander", false);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Directory\shell\MotionCommander", false);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Directory\Background\shell\MotionCommander", false);
            }

            NotifyShellAssociationsChanged();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
