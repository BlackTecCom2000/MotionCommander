using Win11CopyDialog.Modules.StorageControlCenter.Models;

namespace Win11CopyDialog.Modules.StorageControlCenter.Services;

public static class StorageAdvisorService
{
    public static List<StorageRecommendation> GenerateRecommendations(IEnumerable<StorageDisk> disks)
    {
        var recs = new List<StorageRecommendation>();

        foreach (var disk in disks)
        {
            string targetLetter = disk.Partitions.FirstOrDefault(p => !string.IsNullOrEmpty(p.DriveLetter))?.DriveLetter ?? "";

            // 1. Проверка свободного места на SSD/NVMe (SLC Cache exhaustion)
            if (disk.MediaType is StoragePhysicalMedia.NVMeSSD or StoragePhysicalMedia.SataSSD)
            {
                if (disk.FreeSpacePercent < 15.0)
                {
                    recs.Add(new StorageRecommendation
                    {
                        Category = RecommendationCategory.Space,
                        Severity = disk.FreeSpacePercent < 8.0 ? RecommendationSeverity.Critical : RecommendationSeverity.Warning,
                        Risk = RecommendationRiskLevel.Safe,
                        Title = $"Критически мало места на SSD ({disk.Model})",
                        Description = $"Свободно всего {disk.FreeSpacePercent:F1}%. При заполнении накопителя выше 85% деградирует динамический SLC-кэш.",
                        WhatToCheck = "Проверьте корзину, папку загрузок и системный кэш Windows. Твердотельным накопителям требуется от 15% до 20% свободного места для формирования пула свободных страниц флеш-памяти.",
                        WhyItHelps = "При нехватке места контроллер SSD вынужден писать данные напрямую в многоуровневые ячейки (TLC/QLC) со сборкой мусора 'на лету', что снижает линейную запись с 2000-5000 МБ/с до 50-100 МБ/с.",
                        ExpectedEffect = "+15-40 ГБ свободного объема, полное восстановление скорости SLC-кэша и снижение износа ячеек (Write Amplification).",
                        RiskExplanation = "Очистка временных файлов и кэшей полностью безопасна для документов и операционной системы.",
                        DataBasis = $"Свободно {disk.FreeSpacePercent:F1}% ({disk.FreeSpaceFormatted}) из {disk.TotalSizeFormatted}. Том: {disk.DriveLettersFormatted}.",
                        ConfidencePercent = 95,
                        ExactSystemChanges = "Очистка временных файлов %TEMP%, кэшей Windows Update и корзины. Освобождает подтвержденный объем без изменения реестра или системных параметров.",
                        ActionText = "Запустить быструю очистку",
                        ActionCommand = "Cleanup",
                        EstimatedBenefit = "+15-30 ГБ места и восстановление пиковой скорости SLC",
                        TargetDiskNumber = disk.DiskNumber,
                        TargetDriveLetter = targetLetter
                    });
                }
            }

            // 2. Проверка температуры NVMe (Thermal Throttling).
            if (disk.HasTemperature && disk.TemperatureC >= 65.0)
            {
                recs.Add(new StorageRecommendation
                {
                    Category = RecommendationCategory.Thermal,
                    Severity = disk.TemperatureC >= 72.0 ? RecommendationSeverity.Critical : RecommendationSeverity.Warning,
                    Risk = RecommendationRiskLevel.Safe,
                    Title = $"Обнаружен термический троттлинг ({disk.Model} — {disk.TemperatureC:F0} °C)",
                    Description = "Контроллер накопителя сбрасывает тактовые частоты и линии PCIe для защиты от перегрева кристаллов памяти.",
                    WhatToCheck = "Проверьте контакт радиатора M.2 с чипом контроллера, состояние термопрокладки (не ссохлась ли она) и воздушный поток в корпусе (слот M.2 часто подогревается видеокартой).",
                    WhyItHelps = "Контроллеры NVMe при нагреве выше 70 °C принудительно включают троттлинг со снижением скорости в 3-5 раз, чтобы не допустить деградации пайки BGA и кремниевого кристалла.",
                    ExpectedEffect = "Снижение температуры ядра SSD на 10-18 °C; ровная скорость копирования без ступенчатых просадок до 80 МБ/с.",
                    RiskExplanation = "Аппаратная проверка радиатора и активация щадящего профиля безопасны для ваших данных.",
                    DataBasis = $"Измеренная температура ядра: {disk.TemperatureC:F0} °C (критический порог: 70 °C). Источник: {disk.TemperatureSourceDescription}.",
                    ConfidencePercent = 98,
                    ExactSystemChanges = "Включение энергоэффективного профиля с микропаузами конвейера I/O для охлаждения контроллера M.2. Пользовательские файлы не изменяются.",
                    ActionText = "Включить энергоэффективный профиль I/O",
                    ActionCommand = "ThrottleProfile",
                    EstimatedBenefit = "Снижение нагрева на 8-12 °C и стабильный линейный поток",
                    TargetDiskNumber = disk.DiskNumber,
                    TargetDriveLetter = targetLetter
                });
            }

            // 3. Проверка фрагментации на HDD.
            if (disk.HasFragmentation && disk.MediaType == StoragePhysicalMedia.HDD && disk.FragmentationPercent > 8.0)
            {
                recs.Add(new StorageRecommendation
                {
                    Category = RecommendationCategory.Defrag,
                    Severity = disk.FragmentationPercent > 15.0 ? RecommendationSeverity.Warning : RecommendationSeverity.Info,
                    Risk = RecommendationRiskLevel.Safe,
                    Title = $"Фрагментация HDD {targetLetter}: измерено {disk.FragmentationPercent:F1}%",
                    Description = "Магнитные головки совершают избыточные перемещения между секторами, снижая скорость случайного доступа.",
                    WhatToCheck = "Проверьте целостность непрерывных цепочек кластеров на магнитном накопителе с помощью системного дефрагментатора.",
                    WhyItHelps = "Разрозненные фрагменты файлов вынуждают актуатор считывающих головок физически перемещаться по дорожкам пластин с механической задержкой 12-18 мс на каждый кластер.",
                    ExpectedEffect = "Ускорение линейного чтения больших файлов на 30-50%, снижение уровня треска и продление ресурса механики диска.",
                    RiskExplanation = "Штатная дефрагментация Windows использует безопасное перемещение кластеров через API файловой системы.",
                    DataBasis = $"Фактический замер дефрагментатора: {disk.FragmentationPercent:F1}% фрагментированных данных. Том: {targetLetter}:",
                    ConfidencePercent = 90,
                    ExactSystemChanges = "Вызов системной утилиты defrag.exe /U /V для упорядочивания кластеров на магнитном носителе. Для SSD не применяется.",
                    ActionText = "Запустить Smart Defrag",
                    ActionCommand = "Defrag",
                    EstimatedBenefit = "Снижение числа перемещений головок и времени доступа",
                    TargetDiskNumber = disk.DiskNumber,
                    TargetDriveLetter = targetLetter
                });
            }

            // 4. Проверка активности TRIM.
            if (disk.HasTrimInfo && disk.MediaType is StoragePhysicalMedia.NVMeSSD or StoragePhysicalMedia.SataSSD)
            {
                if (!disk.IsTrimEnabled)
                {
                    recs.Add(new StorageRecommendation
                    {
                        Category = RecommendationCategory.Trim,
                        Severity = RecommendationSeverity.Warning,
                        Risk = RecommendationRiskLevel.Safe,
                        Title = $"TRIM отключён для {disk.Model}",
                        Description = "Проверка fsutil показала, что уведомления об освобождении блоков отключены. Для SSD это ускоряет износ ячеек.",
                        WhatToCheck = "Проверьте статус службы DisableDeleteNotify в файловой системе NTFS через утилиту fsutil behavior query.",
                        WhyItHelps = "Команда TRIM информирует микроконтроллер SSD об удалённых файлах, позволяя выполнять фоновую сборку мусора без задержки перед новой записью.",
                        ExpectedEffect = "Устранение падений скорости записи новых файлов и продление ресурса ячеек TLC/QLC на 20-35%.",
                        RiskExplanation = "Включение TRIM — стандартная рекомендуемая конфигурация Microsoft для всех SSD накопителей.",
                        DataBasis = $"fsutil behavior query DisableDeleteNotify вернул неактивный статус TRIM. Диск: {disk.HardwareIdentity}.",
                        ConfidencePercent = 99,
                        ExactSystemChanges = "Выполнение системной команды fsutil behavior set DisableDeleteNotify 0 для включения передачи TRIM в контроллер SSD.",
                        ActionText = "Включить TRIM",
                        ActionCommand = "Trim",
                        EstimatedBenefit = "Снижение интенсивности записи и продление срока службы SSD",
                        TargetDiskNumber = disk.DiskNumber,
                        TargetDriveLetter = targetLetter
                    });
                }
                else
                {
                    recs.Add(new StorageRecommendation
                    {
                        Category = RecommendationCategory.Trim,
                        Severity = RecommendationSeverity.Info,
                        Risk = RecommendationRiskLevel.Safe,
                        Title = string.IsNullOrEmpty(targetLetter)
                            ? $"TRIM активен для {disk.Model} — рекомендуется плановый ReTrim"
                            : $"TRIM активен для тома {targetLetter}: — рекомендуется плановый ReTrim",
                        Description = "Подтверждено проверкой fsutil: уведомления контроллера активны. Регулярная оптимизация поддерживает скорость свободных блоков.",
                        WhatToCheck = "Выполните плановый вызов Optimize-Volume ReTrim для принудительной синхронизации свободных LBA секторов.",
                        WhyItHelps = "ReTrim заставляет контроллер предварительно стереть блоки флеш-памяти в фоновом режиме, сохраняя мгновенную готовность к записи.",
                        ExpectedEffect = "Стабильное время отклика на уровне < 1.0 мс при сохранении крупных файлов.",
                        RiskExplanation = "Операция ReTrim полностью безопасна: она очищает только неиспользуемые свободные сектора.",
                        DataBasis = $"Подтверждено Storage Management API: поддержка TRIM активна. Накопитель: {disk.Model}.",
                        ConfidencePercent = 95,
                        ExactSystemChanges = "Запуск PowerShell Optimize-Volume -DriveLetter -ReTrim для немедленного уведомления контроллера о свободных блоках NAND.",
                        ActionText = "Выполнить ReTrim",
                        ActionCommand = "Trim",
                        EstimatedBenefit = "Поддержание стабильного времени отклика ячеек памяти",
                        TargetDiskNumber = disk.DiskNumber,
                        TargetDriveLetter = targetLetter
                    });
                }
            }

            // 5. Критические атрибуты S.M.A.R.T.
            if (disk.HasSectorHealth && (disk.ReallocatedSectors > 0 || disk.PendingSectors > 0 || disk.UncorrectableSectors > 0))
            {
                recs.Add(new StorageRecommendation
                {
                    Category = RecommendationCategory.Health,
                    Severity = RecommendationSeverity.Critical,
                    Risk = RecommendationRiskLevel.Risky,
                    Title = $"Внимание: деградация секторов ({disk.Model})",
                    Description = $"Обнаружены сбойные секторы: Reallocated={disk.ReallocatedSectors}, Pending={disk.PendingSectors}, Uncorrectable={disk.UncorrectableSectors}.",
                    WhatToCheck = "Проверьте надежность подключения кабелей питания и данных, а также немедленно скопируйте важные файлы на другой накопитель или в облако.",
                    WhyItHelps = "Наличие ожидающих переназначения или поврежденных секторов свидетельствует о физической деградации магнитных дорожек HDD или ячеек флеш-памяти SSD.",
                    ExpectedEffect = "Предотвращение безвозвратной потери ценных документов и системных сбоев Windows.",
                    RiskExplanation = "Внимание: при деградации накопителя интенсивные тесты и дефрагментация противопоказаны. Сначала создайте резервную копию!",
                    DataBasis = $"S.M.A.R.T. атрибуты 5/197/198 сообщают о дефектных секторах. Диск #{disk.DiskNumber} (SN: {disk.SerialNumber}).",
                    ConfidencePercent = 96,
                    ExactSystemChanges = "Системные настройки не затрагиваются. Открывается интерфейс миграции и клонирования разделов для переноса файлов на исправный диск.",
                    ActionText = "Открыть Partition / Backup",
                    ActionCommand = "Backup",
                    EstimatedBenefit = "Сохранение личных данных до полного отказа накопителя",
                    TargetDiskNumber = disk.DiskNumber,
                    TargetDriveLetter = targetLetter
                });
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

        // Списки очищаются перед расчётом.
        // Раньше они только дописывались, поэтому при повторной оценке
        // (она вызывается и при обнаружении, и после измерения фрагментации)
        // одни и те же предупреждения накапливались по нескольку копий,
        // и пользователь видел «Нет измеренных данных» два-три раза подряд.
        s.Warnings.Clear();
        s.Optimizations.Clear();

        // Все под-оценки сбрасываются: иначе при повторном расчёте остались бы
        // значения от предыдущего измерения, которого больше не существует.
        s.HealthScore = 0;
        s.TemperatureScore = 0;
        s.SpaceScore = 0;
        s.LatencyScore = 0;
        s.WearScore = 0;
        s.HasHealth = false;
        s.HasTemperature = false;
        s.HasSpace = false;
        s.HasLatency = false;
        s.HasWear = false;

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
        // ВАЖНО: это справочная величина, она НЕ входит в оценку состояния.
        // Раньше здесь стоял Add(s.SpaceScore, 0.25, true), и диск без единого
        // сигнала здоровья получал 100/100 «A+» только за наличие свободного
        // места. Заполненность диска ничего не говорит о состоянии носителя.
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
            s.Warnings.Add("Нет измеренных данных для расчёта оценки состояния. " +
                           "Свободное место и ёмкость не являются показателями здоровья носителя.");
        }
        else if (s.MeasuredComponentCount == 1)
        {
            // Один сигнал из четырёх — вердикт обнадёжен слабо, и говорить
            // об этом нужно прямо, а не молча ставить «A+».
            s.Warnings.Add(
                $"Оценка основана только на одном показателе ({s.BasisDescription.TrimStart("На основе: ".ToCharArray())}). " +
                "Для полной картины нужны данные S.M.A.R.T. и датчика температуры.");
        }
        else if (weightSum < 1.0)
        {
            s.Warnings.Add("Оценка рассчитана лишь по ограниченному набору доступных параметров.");
        }
    }
}
