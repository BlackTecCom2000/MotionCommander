using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Win11CopyDialog.Modules.StorageControlCenter.Models;

namespace Win11CopyDialog.Modules.StorageControlCenter.Services;

/// <summary>
/// Оркестратор миграции ОС (клонирования Windows).
/// Модуль переработан в рамках Safe Migration Redesign. 
/// Теперь функции разделены на атомарные шаги для вызова из MigrationOrchestratorService.
/// </summary>
public static class OsMigrationService
{
    public static async Task<(bool success, string message)> PrepareTargetDiskAsync(
        StorageDisk targetDisk,
        MigrationPlan plan,
        CancellationToken ct = default)
    {
        if (plan.IsDestructive)
        {
            return await PartitionManagementService.WipeAndCreateSystemPartitionsAsync(targetDisk, plan, "GPT", ct);
        }
        else
        {
            return await PartitionManagementService.CreateSafeOsPartitionAsync(targetDisk.DiskNumber, "GPT", ct);
        }
    }

    public static async Task<(bool success, string message)> CopySystemDataAsync(
        string sourceMountPoint, 
        string targetOsLetter, 
        CancellationToken ct = default)
    {
        try
        {
            // Здесь мы используем Robocopy для надежного системного копирования, так как он сохраняет ACL, потоки и владельцев.
            var psi = new ProcessStartInfo
            {
                FileName = "robocopy.exe",
                Arguments = $"\"{sourceMountPoint}\" \"{targetOsLetter}\" /MIR /SEC /SECFIX /B /MT:32 /R:0 /W:0 /XJ /XD \"System Volume Information\" \"$RECYCLE.BIN\" \"pagefile.sys\" \"swapfile.sys\" \"hiberfil.sys\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true
            };

            using var proc = Process.Start(psi);
            
            while (proc != null && !proc.HasExited)
            {
                if (ct.IsCancellationRequested)
                {
                    proc.Kill();
                    ct.ThrowIfCancellationRequested();
                }
                await Task.Delay(1000, ct);
            }

            // Robocopy коды возврата: < 8 означает успех.
            if (proc?.ExitCode >= 8)
            {
                return (false, "Ошибка копирования файлов (Robocopy вернул код >= 8).");
            }

            // Верификация после копирования
            if (!Directory.Exists(Path.Combine(targetOsLetter, "Windows", "System32")))
            {
                return (false, "КРИТИЧЕСКАЯ ОШИБКА: Копирование завершено, но директория Windows\\System32 не найдена на целевом диске.");
            }

            return (true, "Данные ОС успешно скопированы.");
        }
        catch (Exception ex)
        {
            return (false, $"Ошибка при копировании данных: {ex.Message}");
        }
    }

    public static async Task<(bool success, string message)> SetupBootloaderAsync(
        string targetOsLetter, 
        string targetEfiLetter, 
        CancellationToken ct = default)
    {
        try
        {
            var bcdPsi = new ProcessStartInfo
            {
                FileName = "bcdboot.exe",
                Arguments = $"{targetOsLetter}Windows /s {targetEfiLetter.TrimEnd('\\')} /f ALL",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var bcdProc = Process.Start(bcdPsi);
            await bcdProc.WaitForExitAsync(ct);

            if (bcdProc.ExitCode != 0)
            {
                string bcdErr = await bcdProc.StandardError.ReadToEndAsync(ct);
                return (false, $"Ошибка bcdboot: {bcdErr}");
            }

            return (true, "Загрузчик успешно настроен.");
        }
        catch (Exception ex)
        {
            return (false, $"Ошибка при настройке загрузчика: {ex.Message}");
        }
    }

    public static async Task HideTemporaryLettersAsync(int targetDiskNumber, CancellationToken ct)
    {
        try
        {
            string script = $@"
select disk {targetDiskNumber}
select volume S
remove letter=S
select volume W
remove letter=W
";
            string tempPath = Path.Combine(Path.GetTempPath(), "remove_letters.txt");
            await File.WriteAllTextAsync(tempPath, script, ct);
            
            var psi = new ProcessStartInfo("diskpart.exe", $"/s \"{tempPath}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            
            using var proc = Process.Start(psi);
            await proc?.WaitForExitAsync(ct)!;
            
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
        catch 
        {
            // Ignore errors here, this is just a cleanup
        }
    }
}
