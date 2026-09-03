using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace Win11CopyDialog.Modules.WindowsShellIntegration;

/// <summary>
/// Сервис интеграции с Windows Explorer (контекстное меню, ассоциации файлов).
/// Выполняет запись в HKCU (HKEY_CURRENT_USER\Software\Classes), не требуя прав Администратора.
/// </summary>
public static class ShellIntegrationService
{
    private const string AppTitle = "Motion Commander";

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

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
