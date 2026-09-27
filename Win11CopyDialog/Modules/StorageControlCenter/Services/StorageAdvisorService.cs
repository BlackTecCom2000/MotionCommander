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

            // 2. Проверка температуры NVMe (Thermal Throttling).
            //    Раньше срабатывало на подставном значении 41/34/36/31 °C,
            //    то есть практически никогда. Теперь — только при реальном замере.
            if (disk.HasTemperature && disk.TemperatureC >= 65.0)
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

            // 3. Проверка фрагментации на HDD.
            //    Раньше фрагментация была подставной константой (4.8% для HDD),
            //    и реальный анализатор defrag /A не вызывался вообще.
            if (disk.HasFragmentation && disk.MediaType == StoragePhysicalMedia.HDD && disk.FragmentationPercent > 8.0)
            {
                string targetLetter = disk.Partitions.FirstOrDefault(p => !string.IsNullOrEmpty(p.DriveLetter))?.DriveLetter ?? "";
                recs.Add(new StorageRecommendation
                {
                    Category = RecommendationCategory.Defrag,
                    Severity = disk.FragmentationPercent > 15.0 ? RecommendationSeverity.Warning : RecommendationSeverity.Info,
                    Title = $"Фрагментация HDD {targetLetter}: измерено {disk.FragmentationPercent:F1}%",
                    Description = "Магнитные головки совершают избыточные перемещения между секторами, снижая скорость случайного доступа.",
                    ActionText = "Запустить Smart Defrag",
                    ActionCommand = "Defrag",
                    EstimatedBenefit = "Снижение числа перемещений головок и времени доступа",
                    TargetDiskNumber = disk.DiskNumber,
                    TargetDriveLetter = targetLetter
                });
            }

            // 4. Проверка активности TRIM.
            //    Раньше IsTrimSupported всегда был true по умолчанию, поэтому
            //    рекомендация выдавалась даже для накопителей без TRIM.
            if (disk.HasTrimInfo && disk.MediaType is StoragePhysicalMedia.NVMeSSD or StoragePhysicalMedia.SataSSD)
            {
                if (!disk.IsTrimEnabled)
                {
                    recs.Add(new StorageRecommendation
                    {
                        Category = RecommendationCategory.Trim,
                        Severity = RecommendationSeverity.Warning,
                        Title = $"TRIM отключён для {disk.Model}",
                        Description = "Проверка fsutil показала, что уведомления об освобождении блоков отключены. Для SSD это ускоряет износ ячеек.",
                        ActionText = "Включить TRIM",
                        ActionCommand = "Trim",
                        EstimatedBenefit = "Снижение интенсивности записи и продление срока службы SSD",
                        TargetDiskNumber = disk.DiskNumber
                    });
                }
                else
                {
                    string targetLetter = disk.Partitions.FirstOrDefault(p => !string.IsNullOrEmpty(p.DriveLetter))?.DriveLetter ?? "";
                    recs.Add(new StorageRecommendation
                    {
                        Category = RecommendationCategory.Trim,
                        Severity = RecommendationSeverity.Info,
                        Title = string.IsNullOrEmpty(targetLetter)
                            ? "TRIM включён — рекомендуется регулярная оптимизация"
                            : $"TRIM включён для {targetLetter} — рекомендуется регулярная оптимизация",
                        Description = "Подтверждено проверкой fsutil: команда TRIM информирует контроллер об освободившихся блоках для фоновой сборки мусора.",
                        ActionText = "Выполнить ReTrim",
                        ActionCommand = "Trim",
                        EstimatedBenefit = "Поддержание стабильного времени отклика ячеек памяти",
                        TargetDiskNumber = disk.DiskNumber,
                        TargetDriveLetter = targetLetter
                    });
                }
            }
        }

        return recs;
    }

    /// <summary>
    /// Рассчитывает итоговую оценку накопителя.
    ///
    /// <para>Ключевое изменение: рассчитываются ТОЛЬКО те компоненты, для которых
    /// есть реальные измеренные данные. Нерассчитанные компоненты исключаются
    /// из взвешенной суммы, а не считаются нулевыми (это занижало бы оценку) и
    /// не считаются сотней (это завышало бы её).</para>
    ///
    /// <para>Раньше HealthStatus всегда равнялся дефолтному «Healthy», поэтому
    /// HealthScore был ровно 100 у любого диска, а вместе с подставными
    /// температурой и износом итог всегда выходил «A+».</para>
    /// </summary>
    public static void EvaluateScore(StorageDisk disk)
    {
        var s = disk.Score;

        double weightSum = 0;
        double weightedSum = 0;

        void Add(double value, double weight, bool measured)
        {
            if (!measured) return;
            weightedSum += value * weight;
            weightSum += weight;
        }

        // ── Здоровье: из реального MSFT_Disk.HealthStatus ──────────────────
        string health = disk.HealthStatus?.Trim() ?? "";
        if (health.Length > 0)
        {
            double h = health.ToLowerInvariant() switch
            {
                "healthy" or "ok" or "good" => 100,
                "warning" or "caution" or "degraded" => 55,
                "unhealthy" or "failed" or "critical" => 0,
                _ => -1   // неизвестное значение — не оцениваем
            };

            if (h >= 0)
            {
                s.HealthScore = h;
                s.HasHealth = true;
                Add(h, 0.30, true);
            }
        }

        // ── Дополнительно учитываем критические SMART-ошибки ────────────────
        // Переназначенные, ожидающие и неустранимые секторы важнее любой шкалы.
        if (disk.HasSmartAttributes)
        {
            var critical = disk.SmartAttributes.Count(a => a.Status == "Critical");
            if (critical > 0)
            {
                // Пересчитываем здоровье с учётом реальных ошибок носителя.
                double penalty = Math.Min(50, critical * 15);
                s.HealthScore = s.HasHealth ? Math.Max(0, s.HealthScore - penalty) : 0;
                s.HasHealth = true;
                s.Warnings.Add($"S.M.A.R.T: {critical} критических атрибутов (переназначенные/ожидающие/неустранимые секторы).");
            }
        }

        // ── Температура: только если реально измерена ──────────────────────
        if (disk.HasTemperature)
        {
            s.TemperatureScore = disk.TemperatureC switch
            {
                <= 48 => 100,
                <= 60 => 85,
                <= 70 => 60,
                _ => 30
            };
            s.HasTemperature = true;
            Add(s.TemperatureScore, 0.20, true);
        }

        // ── Свободное место: реальные данные разделов ─────────────────────
        if (disk.TotalSizeBytes > 0 && disk.Partitions.Count > 0)
        {
            s.SpaceScore = disk.FreeSpacePercent switch
            {
                >= 25 => 100,
                >= 15 => 85,
                >= 8 => 60,
                _ => 35
            };
            s.HasSpace = true;
            Add(s.SpaceScore, 0.25, true);
        }

        // ── Задержка: только если есть реальная телеметрия нагрузки ───────
        if (disk.CurrentLatencyMs > 0)
        {
            s.LatencyScore = disk.CurrentLatencyMs switch
            {
                <= 5.0 => 100,
                <= 20.0 => 85,
                <= 50.0 => 60,
                _ => 40
            };
            s.HasLatency = true;
            Add(s.LatencyScore, 0.15, true);
        }

        // ── Износ: только если реально измерен ────────────────────────────
        if (disk.HasWear)
        {
            s.WearScore = Math.Max(0, 100 - disk.WearLevelPercent);
            s.HasWear = true;
            Add(s.WearScore, 0.10, true);
        }

        // Итог — взвешенное среднее ТОЛЬКО по рассчитанным компонентам.
        s.TotalScore = weightSum > 0
            ? Math.Round(weightedSum / weightSum, 1)
            : 0;

        if (!s.IsCalculated)
        {
            s.TotalScore = 0;
            s.Warnings.Add("Нет измеренных данных для расчёта оценки состояния.");
        }
        else if (weightSum < 1.0)
        {
            s.Warnings.Add("Оценка рассчитана лишь по ограниченному набору доступных параметров.");
        }
    }
}
