using System.IO;
using Win11CopyDialog.Modules.StorageControlCenter.Models;

namespace Win11CopyDialog.Modules.StorageControlCenter.Services;

public static class StorageCleanupService
{
    public static async Task<List<StorageCleanupItem>> ScanCleanupCategoriesAsync(CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var list = new List<StorageCleanupItem>();

            // 1. User Temp
            string userTemp = Path.GetTempPath();
            var (userTempBytes, userTempCount) = CalculateDirectoryUsage(userTemp, ct);
            list.Add(new StorageCleanupItem
            {
                Name = "Временные файлы пользователя (User %TEMP%)",
                Description = "Кэш инсталляторов, остатки временных архивов и скриптов",
                PathLocation = userTemp,
                SizeBytes = userTempBytes,
                FileCount = userTempCount,
                IsSelected = true,
                RiskLevel = "SAFE"
            });

            // 2. System Temp
            string winTemp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
            var (winTempBytes, winTempCount) = CalculateDirectoryUsage(winTemp, ct);
            list.Add(new StorageCleanupItem
            {
                Name = "Системные временные файлы (Windows Temp)",
                Description = "Временные файлы системных служб и драйверов Windows",
                PathLocation = winTemp,
                SizeBytes = winTempBytes,
                FileCount = winTempCount,
                IsSelected = true,
                RiskLevel = "SAFE"
            });

            // 3. Crash Dumps
            string crashDumps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps");
            var (dumpBytes, dumpCount) = CalculateDirectoryUsage(crashDumps, ct);
            list.Add(new StorageCleanupItem
            {
                Name = "Дампы аварийных сбоев (Crash Dumps)",
                Description = "Автоматические дампы памяти упавших программ и служб",
                PathLocation = crashDumps,
                SizeBytes = dumpBytes,
                FileCount = dumpCount,
                IsSelected = true,
                RiskLevel = "SAFE"
            });

            // 4. Delivery Optimization
            string deliveryOpt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SoftwareDistribution", "DeliveryOptimization");
            var (delivBytes, delivCount) = CalculateDirectoryUsage(deliveryOpt, ct);
            list.Add(new StorageCleanupItem
            {
                Name = "Кэш оптимизации доставки Windows (Delivery Optimization)",
                Description = "Промежуточные файлы обновлений Windows P2P",
                PathLocation = deliveryOpt,
                SizeBytes = delivBytes,
                FileCount = delivCount,
                IsSelected = false,
                RiskLevel = "SAFE"
            });

            return list;
        }, ct);
    }

    public static async Task<(long cleanedBytes, int deletedFiles)> CleanSelectedAsync(
        IEnumerable<StorageCleanupItem> items,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            long totalBytes = 0;
            int totalFiles = 0;

            foreach (var item in items.Where(i => i.IsSelected))
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Очистка: {item.Name}...");

                if (!Directory.Exists(item.PathLocation)) continue;

                try
                {
                    var di = new DirectoryInfo(item.PathLocation);
                    foreach (var f in di.GetFiles("*", SearchOption.TopDirectoryOnly))
                    {
                        try
                        {
                            // Пропускаем заблокированные файлы
                            long len = f.Length;
                            f.Delete();
                            totalBytes += len;
                            totalFiles++;
                        }
                        catch { }
                    }

                    foreach (var d in di.GetDirectories("*", SearchOption.TopDirectoryOnly))
                    {
                        try
                        {
                            d.Delete(true);
                        }
                        catch { }
                    }
                }
                catch { }
            }

            PartitionManagementService.OperationLogs.Insert(0, new StorageOperationLog
            {
                OperationName = "Очистка системного кэша",
                TargetDescription = "Временные каталоги",
                RiskLevel = StorageRiskLevel.SAFE,
                Status = "Успешно",
                Details = $"Освобождено {totalBytes / (1024 * 1024)} МБ ({totalFiles} файлов)."
            });

            return (totalBytes, totalFiles);
        }, ct);
    }

    private static (long bytes, int count) CalculateDirectoryUsage(string path, CancellationToken ct)
    {
        if (!Directory.Exists(path)) return (0, 0);

        long bytes = 0;
        int count = 0;

        try
        {
            var di = new DirectoryInfo(path);
            foreach (var f in di.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    bytes += f.Length;
                    count++;
                }
                catch { }
            }
        }
        catch { }

        return (bytes, count);
    }
}
