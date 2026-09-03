using Win11CopyDialog.Modules.StorageControlCenter.Models;

namespace Win11CopyDialog.Modules.StorageControlCenter.Services;

public static class StorageAdvisorService
{
    public static List<StorageRecommendation> GenerateRecommendations(IEnumerable<StorageDisk> disks)
    {
        var recs = new List<StorageRecommendation>();

        foreach (var disk in disks)
        {
            // 1. Проверка свободного места на SSD/NVMe (SLC Cache exhaustion)
            if (disk.MediaType is StoragePhysicalMedia.NVMeSSD or StoragePhysicalMedia.SataSSD)
            {
                if (disk.FreeSpacePercent < 15.0)
                {
                    recs.Add(new StorageRecommendation
                    {
                        Category = RecommendationCategory.Space,
                        Severity = disk.FreeSpacePercent < 8.0 ? RecommendationSeverity.Critical : RecommendationSeverity.Warning,
                        Title = $"Критически мало места на SSD ({disk.Model})",
                        Description = $"Свободно всего {disk.FreeSpacePercent:F1}%. При заполнении твердотельного накопителя выше 85% деградирует динамический SLC-кэш, что снижает скорость записи до 4-6 раз.",
                        ActionText = "Запустить быструю очистку",
                        ActionCommand = "Cleanup",
                        EstimatedBenefit = "+15-30 ГБ места и восстановление пиковой скорости SLC",
                        TargetDiskNumber = disk.DiskNumber
                    });
                }
            }

            // 2. Проверка температуры NVMe (Thermal Throttling)
            if (disk.TemperatureC >= 65.0)
            {
                recs.Add(new StorageRecommendation
                {
                    Category = RecommendationCategory.Thermal,
                    Severity = disk.TemperatureC >= 72.0 ? RecommendationSeverity.Critical : RecommendationSeverity.Warning,
                    Title = $"Обнаружен термический троттлинг ({disk.Model} — {disk.TemperatureC:F0} °C)",
                    Description = "Контроллер накопителя сбрасывает тактовые частоты и линии PCIe для защиты от перегрева кристаллов памяти.",
                    ActionText = "Включить энергоэффективный профиль I/O",
                    ActionCommand = "ThrottleProfile",
                    EstimatedBenefit = "Снижение нагрева на 8-12 °C и стабильный линейный поток",
                    TargetDiskNumber = disk.DiskNumber
                });
            }

            // 3. Проверка фрагментации на HDD
            if (disk.MediaType == StoragePhysicalMedia.HDD && disk.FragmentationPercent > 8.0)
            {
                string targetLetter = disk.Partitions.FirstOrDefault(p => !string.IsNullOrEmpty(p.DriveLetter))?.DriveLetter ?? "D";
                recs.Add(new StorageRecommendation
                {
                    Category = RecommendationCategory.Defrag,
                    Severity = disk.FragmentationPercent > 15.0 ? RecommendationSeverity.Warning : RecommendationSeverity.Info,
                    Title = $"Фрагментация HDD диска {targetLetter}: составляет {disk.FragmentationPercent:F1}%",
                    Description = "Магнитные головки совершают избыточные перемещения между секторами, снижая скорость случайного доступа на 35-50%.",
                    ActionText = "Запустить Smart Defrag",
                    ActionCommand = "Defrag",
                    EstimatedBenefit = "+40% к скорости чтения файлов и снижение шума головок",
                    TargetDiskNumber = disk.DiskNumber,
                    TargetDriveLetter = targetLetter
                });
            }

            // 4. Проверка активности TRIM
            if ((disk.MediaType is StoragePhysicalMedia.NVMeSSD or StoragePhysicalMedia.SataSSD) && disk.IsTrimSupported)
            {
                string targetLetter = disk.Partitions.FirstOrDefault(p => !string.IsNullOrEmpty(p.DriveLetter))?.DriveLetter ?? "C";
                recs.Add(new StorageRecommendation
                {
                    Category = RecommendationCategory.Trim,
                    Severity = RecommendationSeverity.Info,
                    Title = $"Регулярная оптимизация TRIM для {targetLetter}:",
                    Description = "Команда TRIM информирует контроллер SSD об освободившихся блоках LBA для своевременной фоновой сборки мусора (Garbage Collection).",
                    ActionText = "Выполнить ReTrim",
                    ActionCommand = "Trim",
                    EstimatedBenefit = "Поддержание стабильного времени отклика ячеек памяти",
                    TargetDiskNumber = disk.DiskNumber,
                    TargetDriveLetter = targetLetter
                });
            }
        }

        return recs;
    }

    public static void EvaluateScore(StorageDisk disk)
    {
        var s = disk.Score;

        // Health Score
        s.HealthScore = disk.HealthStatus.Equals("Healthy", StringComparison.OrdinalIgnoreCase) ? 100 : 65;

        // Temperature Score
        if (disk.TemperatureC <= 48) s.TemperatureScore = 100;
        else if (disk.TemperatureC <= 60) s.TemperatureScore = 85;
        else if (disk.TemperatureC <= 70) s.TemperatureScore = 60;
        else s.TemperatureScore = 30;

        // Space Score
        if (disk.FreeSpacePercent >= 25) s.SpaceScore = 100;
        else if (disk.FreeSpacePercent >= 15) s.SpaceScore = 85;
        else if (disk.FreeSpacePercent >= 8) s.SpaceScore = 60;
        else s.SpaceScore = 35;

        // Latency Score
        if (disk.CurrentLatencyMs <= 5.0) s.LatencyScore = 100;
        else if (disk.CurrentLatencyMs <= 20.0) s.LatencyScore = 85;
        else if (disk.CurrentLatencyMs <= 50.0) s.LatencyScore = 60;
        else s.LatencyScore = 40;

        // Wear Score
        s.WearScore = Math.Max(0, 100 - disk.WearLevelPercent);

        // Weighted Average
        s.TotalScore = Math.Round(
            s.HealthScore * 0.30 +
            s.TemperatureScore * 0.20 +
            s.SpaceScore * 0.25 +
            s.LatencyScore * 0.15 +
            s.WearScore * 0.10, 1);
    }
}
