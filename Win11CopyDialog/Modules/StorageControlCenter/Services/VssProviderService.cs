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
    /// Создает теневую копию указанного диска (например, "C:\") и монтирует её в указанную папку (симлинк).
    /// Возвращает ID теневой копии для последующего удаления.
    /// </summary>
    public static async Task<(bool success, string shadowId, string message)> CreateAndMountShadowCopyAsync(string sourceDrive, string mountPointPath, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            try
            {
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

                string shadowId = outParams?["ShadowID"]?.ToString() ?? "";

                // 2. Получаем DeviceObject теневой копии
                string deviceObject = string.Empty;
                var searcher = new ManagementObjectSearcher($"SELECT * FROM Win32_ShadowCopy WHERE ID = '{shadowId}'");
                foreach (ManagementObject queryObj in searcher.Get())
                {
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
                proc?.WaitForExit();

                if (proc?.ExitCode != 0)
                {
                    return (false, shadowId, $"Ошибка создания симлинка (mklink): {proc?.StandardError.ReadToEnd()}");
                }

                return (true, shadowId, "Теневая копия успешно создана и смонтирована.");
            }
            catch (Exception ex)
            {
                return (false, string.Empty, $"Исключение при работе с VSS: {ex.Message}");
            }
        }, ct);
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
