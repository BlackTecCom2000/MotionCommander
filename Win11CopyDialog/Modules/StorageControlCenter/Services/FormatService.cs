using System.Diagnostics;
using Win11CopyDialog.Modules.StorageControlCenter.Models;

namespace Win11CopyDialog.Modules.StorageControlCenter.Services;

public static class FormatService
{
    public static async Task<(bool success, string message)> FormatVolumeAsync(
        string driveLetter,
        string fileSystem = "NTFS",
        string volumeLabel = "NewVolume",
        int allocationUnitSize = 4096,
        bool quickFormat = true,
        CancellationToken ct = default)
    {
        string letter = driveLetter.TrimEnd('\\', ':').ToUpperInvariant();

        // Критическая защита системного диска
        if (letter == "C" || letter == Environment.GetFolderPath(Environment.SpecialFolder.Windows).Substring(0, 1).ToUpperInvariant())
        {
            throw new InvalidOperationException("КРИТИЧЕСКАЯ ЗАЩИТА: Форматирование системного тома Windows (C:) категорически запрещено.");
        }

        string quickArg = quickFormat ? "-Full:$false" : "-Full:$true";
        string script = $"Format-Volume -DriveLetter '{letter}' -FileSystem {fileSystem} -NewFileSystemLabel '{volumeLabel}' -AllocationUnitSize {allocationUnitSize} {quickArg} -Confirm:$false";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = new Process { StartInfo = psi };
            proc.Start();
            string stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            string stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            bool ok = proc.ExitCode == 0;
            string msg = ok ? $"Том {letter}: успешно отформатирован в {fileSystem}." : (!string.IsNullOrWhiteSpace(stderr) ? stderr : stdout);

            PartitionManagementService.OperationLogs.Insert(0, new StorageOperationLog
            {
                OperationName = "Форматирование тома",
                TargetDescription = $"{letter}: ({fileSystem})",
                RiskLevel = StorageRiskLevel.DESTRUCTIVE,
                Status = ok ? "Успешно" : "Ошибка",
                Details = msg.Trim()
            });

            return (ok, msg);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
