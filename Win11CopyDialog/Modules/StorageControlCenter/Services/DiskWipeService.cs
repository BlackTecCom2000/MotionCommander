using System.IO;
using Win11CopyDialog.Modules.StorageControlCenter.Models;

namespace Win11CopyDialog.Modules.StorageControlCenter.Services;

public static class DiskWipeService
{
    public static async Task<(bool success, string message)> WipeFreeSpaceAsync(
        string targetDriveLetter,
        IProgress<(int percent, string status, double speedMBps)>? progress = null,
        CancellationToken ct = default)
    {
        string letter = targetDriveLetter.TrimEnd('\\', ':').ToUpperInvariant() + ":\\";
        var driveInfo = new DriveInfo(letter);
        if (!driveInfo.IsReady) return (false, "Диск не готов");

        long freeBytes = driveInfo.AvailableFreeSpace;
        // Оставляем 200 МБ для безопасности ОС
        long bytesToWipe = Math.Max(0, freeBytes - 200L * 1024 * 1024);
        if (bytesToWipe <= 0) return (false, "Недостаточно свободного места для процедуры затирания.");

        string wipeFilePath = Path.Combine(letter, $"__mc_wipe_{Guid.NewGuid():N}.tmp");
        int bufferSize = 4 * 1024 * 1024; // 4 MB буфер
        byte[] zeroBuffer = new byte[bufferSize];

        long totalWritten = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            await using (var fs = new FileStream(wipeFilePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                while (totalWritten < bytesToWipe)
                {
                    ct.ThrowIfCancellationRequested();
                    int toWrite = (int)Math.Min(bufferSize, bytesToWipe - totalWritten);
                    await fs.WriteAsync(zeroBuffer.AsMemory(0, toWrite), ct);
                    totalWritten += toWrite;

                    int pct = (int)((double)totalWritten / bytesToWipe * 100.0);
                    double sec = Math.Max(0.001, sw.Elapsed.TotalSeconds);
                    double speed = (totalWritten / (1024.0 * 1024.0)) / sec;

                    progress?.Report((pct, $"Затирание нулями: {totalWritten / (1024 * 1024)} МБ из {bytesToWipe / (1024 * 1024)} МБ", speed));
                }
                await fs.FlushAsync(ct);
            }

            PartitionManagementService.OperationLogs.Insert(0, new StorageOperationLog
            {
                OperationName = "Затирание свободного места",
                TargetDescription = letter,
                RiskLevel = StorageRiskLevel.SAFE,
                Status = "Успешно",
                Details = $"Перезаписано {totalWritten / (1024 * 1024)} МБ удаленного пространства."
            });

            return (true, $"Свободное пространство диска {letter} ({totalWritten / (1024 * 1024)} МБ) успешно очищено от остаточных данных.");
        }
        catch (OperationCanceledException)
        {
            return (false, "Операция отменена пользователем.");
        }
        catch (Exception ex)
        {
            return (false, $"Ошибка затирания: {ex.Message}");
        }
        finally
        {
            try
            {
                if (File.Exists(wipeFilePath)) File.Delete(wipeFilePath);
            }
            catch { }
        }
    }
}
