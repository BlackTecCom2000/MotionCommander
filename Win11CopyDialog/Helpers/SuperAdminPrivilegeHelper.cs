using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Win11CopyDialog.Helpers;

/// <summary>
/// Обеспечивает повышение системных привилегий токена процесса до уровня Super-Admin (God-tier / Kernel privileges)
/// для безопасного и прямого доступа ко всем дискам, разделам и защищенным томам Windows.
/// </summary>
public static class SuperAdminPrivilegeHelper
{
    private const int TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const int TOKEN_QUERY = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public int PrivilegeCount;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
        public LUID_AND_ATTRIBUTES[] Privileges;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, int DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr TokenHandle,
        bool DisableAllPrivileges,
        ref TOKEN_PRIVILEGES NewState,
        int BufferLength,
        IntPtr PreviousState,
        IntPtr ReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// Проверяет, запущен ли процесс с правами администратора Windows.
    /// </summary>
    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Активирует все ключевые привилегии Windows в токене процесса для доступа лучше, чем у администратора.
    /// </summary>
    public static void EnableAllSuperAdminPrivileges()
    {
        string[] privileges = new[]
        {
            "SeManageVolumePrivilege",       // Прямой контроль над томами, блокировкой и форматированием
            "SeTakeOwnershipPrivilege",      // Захват владения объектами и томами
            "SeBackupPrivilege",             // Обход любых ограничений на чтение секторов
            "SeRestorePrivilege",            // Обход любых ограничений на запись секторов
            "SeDebugPrivilege",              // Отладка и захват системных дескрипторов
            "SeSecurityPrivilege",           // Управление аудитом безопасности
            "SeSystemEnvironmentPrivilege",  // Доступ к системным переменным и NVRAM
            "SeIncreaseBasePriorityPrivilege"// Высокий приоритет ввода-вывода
        };

        IntPtr hToken = IntPtr.Zero;
        try
        {
            if (OpenProcessToken(Process.GetCurrentProcess().Handle, TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out hToken))
            {
                foreach (var priv in privileges)
                {
                    if (LookupPrivilegeValue(null, priv, out LUID luid))
                    {
                        var tp = new TOKEN_PRIVILEGES
                        {
                            PrivilegeCount = 1,
                            Privileges = new LUID_AND_ATTRIBUTES[1]
                            {
                                new LUID_AND_ATTRIBUTES
                                {
                                    Luid = luid,
                                    Attributes = SE_PRIVILEGE_ENABLED
                                }
                            }
                        };
                        AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                    }
                }
            }
        }
        catch
        {
            // Неблокирующее логирование при отсутствии прав
        }
        finally
        {
            if (hToken != IntPtr.Zero)
            {
                CloseHandle(hToken);
            }
        }
    }
}
