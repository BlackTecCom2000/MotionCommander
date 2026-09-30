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
        string efiLetter = "S",
        string osLetter = "W",
        CancellationToken ct = default)
    {
        char efi = char.ToUpperInvariant(efiLetter.TrimEnd('\\', ':').FirstOrDefault());
        char os = char.ToUpperInvariant(osLetter.TrimEnd('\\', ':').FirstOrDefault());
        if (efi == '\0') efi = 'S';
        if (os == '\0') os = 'W';

        if (plan.IsDestructive)
        {
            return await PartitionManagementService.WipeAndCreateSystemPartitionsAsync(targetDisk, plan, "GPT", ct, efi, os);
        }
        else
        {
            return await PartitionManagementService.CreateSafeOsPartitionAsync(targetDisk.DiskNumber, "GPT", ct, efi, os);
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
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                return (false, "Не удалось запустить robocopy.exe.");
            }

            // Важно: асинхронно вычитываем stdout и stderr, чтобы robocopy не зависал из-за переполнения буфера pipe (deadlock)
            var readOutputTask = proc.StandardOutput.ReadToEndAsync(ct);
            var readErrorTask = proc.StandardError.ReadToEndAsync(ct);

            try
            {
                await proc.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { if (!proc.HasExited) proc.Kill(true); } catch { }
                throw;
            }

            await Task.WhenAll(readOutputTask, readErrorTask);

            // Robocopy коды возврата: < 8 означает успех (0-7: скопировано / совпадало).
            if (proc.ExitCode >= 8)
            {
                string errOutput = await readErrorTask;
                return (false, $"Ошибка копирования файлов (Robocopy вернул код {proc.ExitCode}): {errOutput}");
            }

            // Верификация после копирования
            if (!Directory.Exists(Path.Combine(targetOsLetter, "Windows", "System32")))
            {
                return (false, "КРИТИЧЕСКАЯ ОШИБКА: Копирование завершено, но директория Windows\\System32 не найдена на целевом диске.");
            }

            return (true, "Данные ОС успешно скопированы.");
        }
        catch (OperationCanceledException)
        {
            throw;
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

            if (bcdProc == null)
            {
                return (false, "Не удалось запустить bcdboot.exe. Убедитесь, что файл присутствует в системе и у процесса есть права администратора.");
            }

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

    public static async Task HideTemporaryLettersAsync(
        int targetDiskNumber, 
        string efiLetter = "S", 
        string osLetter = "W", 
        CancellationToken ct = default)
    {
        try
        {
            char efi = char.ToUpperInvariant(efiLetter.TrimEnd('\\', ':').FirstOrDefault());
            char os = char.ToUpperInvariant(osLetter.TrimEnd('\\', ':').FirstOrDefault());

            var scriptBuilder = new System.Text.StringBuilder();
            scriptBuilder.AppendLine($"select disk {targetDiskNumber}");
            if (efi != '\0')
            {
                scriptBuilder.AppendLine($"select volume {efi}");
                scriptBuilder.AppendLine($"remove letter={efi}");
            }
            if (os != '\0')
            {
                scriptBuilder.AppendLine($"select volume {os}");
                scriptBuilder.AppendLine($"remove letter={os}");
            }

            string tempPath = Path.Combine(Path.GetTempPath(), $"remove_letters_{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(tempPath, scriptBuilder.ToString(), ct);
            
            var psi = new ProcessStartInfo("diskpart.exe", $"/s \"{tempPath}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                await proc.WaitForExitAsync(ct);
            }
            
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
        catch 
        {
            // Ignore errors here, this is just a cleanup
        }
    }
}
