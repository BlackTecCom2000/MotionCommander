namespace Win11CopyDialog.Modules.StorageControlCenter.Models;

public enum StoragePhysicalBus
{
    NVMe,
    SATA,
    USB,
    SAS,
    RAID,
    Virtual,
    Unknown
}

public enum StoragePhysicalMedia
{
    NVMeSSD,
    SataSSD,
    HDD,
    USBFlash,
    VirtualDisk,
    Unknown
}

/// <summary>
/// Источник диагностических данных.
///
/// <para>Критично важно: приложение обязано отличать ИЗМЕРЕННЫЕ значения от
/// предполагаемых. Раньше это различие отсутствовало, и при недоступности
/// WMI подставлялась таблица «правдоподобных» констант (41 °C для NVMe,
/// 1840 часов наработки и т.д.), которые пользователь принимал за реальные
/// показания.</para>
/// </summary>
public enum TelemetrySource
{
    /// <summary>Значение не измерено — контроллер не отдаёт такие данные.</summary>
    Unavailable,

    /// <summary>Измерено через WMI/CIM (MSFT_*, Win32_*).</summary>
    Wmi,

    /// <summary>Измерено через SMART-таблицу ATA/NVMe.</summary>
    Smart,

    /// <summary>Измерено запуском системной утилиты (fsutil, defrag, chkdsk).</summary>
    Utility
}

public sealed class StorageDisk
{
    public int DiskNumber { get; set; }
    public string Model { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public StoragePhysicalBus BusType { get; set; } = StoragePhysicalBus.Unknown;
    public StoragePhysicalMedia MediaType { get; set; } = StoragePhysicalMedia.Unknown;

    /// <summary>
    /// Схема разметки. Раньше по умолчанию стояло «GPT» — то есть диск без
    /// запроса WMI объявлялся GPT. Теперь значение по умолчанию неизвестно.
    /// </summary>
    public string PartitionStyle { get; set; } = "Неизвестно";

    public long TotalSizeBytes { get; set; }
    public long AllocatedSizeBytes { get; set; }
    public long UnallocatedSizeBytes => Math.Max(0, TotalSizeBytes - AllocatedSizeBytes);

    /// <summary>
    /// Реальное состояние здоровья из MSFT_Disk.HealthStatus
    /// (Healthy / Warning / Unhealthy) или Win32_DiskDrive.Status.
    /// </summary>
    public string HealthStatus { get; set; } = "";

    /// <summary>Реальное состояние работоспособности из MSFT_Disk.OperationalStatus.</summary>
    public string OperationalStatus { get; set; } = "";

    /// <summary>Температура в градусах Цельсия. Актуальна только при HasTemperature.</summary>
    public double TemperatureC { get; set; }

    /// <summary>Износ в процентах (0 = новый, 100 = выработал ресурс).</summary>
    public double WearLevelPercent { get; set; }
    public double LifetimeRemainingPercent => Math.Max(0, 100.0 - WearLevelPercent);

    public long PowerOnHours { get; set; }
    public long PowerCycles { get; set; }
    public long UnsafeShutdowns { get; set; }
    public long TotalBytesWritten { get; set; }
    public long TotalBytesRead { get; set; }
    public long ReadErrorsTotal { get; set; }
    public long WriteErrorsTotal { get; set; }

    public bool IsSystemDisk { get; set; }

    /// <summary>
    /// Реально измеренные характеристики.
    ///
    /// <para>Каждый флаг соответствует отдельному полю. Пока флаг не установлен,
    /// соответствующее значение НЕ показывается пользователю — вместо него
    /// выводится «Нет данных». Это устраняет главную проблему: приложение
    /// больше не «угадывает» показания накопителя.</para>
    /// </summary>
    public bool HasTemperature { get; set; }
    public bool HasWear { get; set; }
    public bool HasPowerOnHours { get; set; }
    public bool HasPowerCycles { get; set; }
    public bool HasLifetimeWrites { get; set; }
    public bool HasUnsafeShutdowns { get; set; }
    public bool HasErrorCounts { get; set; }
    public bool HasTrimInfo { get; set; }
    public bool HasFragmentation { get; set; }
    public bool HasSmartAttributes { get; set; }
    public bool HasSectorAlignment { get; set; }

    /// <summary>Поддержка TRIM реально подтверждена системой.</summary>
    public bool IsTrimSupported { get; set; }

    /// <summary>TRIM реально включён (проверено через fsutil).</summary>
    public bool IsTrimEnabled { get; set; }

    /// <summary>Выравнивание логического сектора реально проверено.</summary>
    public bool Is4KAligned { get; set; }

    /// <summary>Фрагментация в процентах, измеренная дефрагментатором.</summary>
    public double FragmentationPercent { get; set; }

    /// <summary>Откуда получены показатели надёжности.</summary>
    public TelemetrySource Source { get; set; } = TelemetrySource.Unavailable;

    /// <summary>
    /// Пояснение для пользователя: почему данных нет. Например
    /// «Контроллер не поддерживает S.M.A.R.T. (RAID/VirtIO)».
    /// </summary>
    public string TelemetryNote { get; set; } = "";

    /// <summary>Истина, если хотя бы один показатель реально измерен.</summary>
    public bool HasRealTelemetry =>
        HasTemperature || HasWear || HasPowerOnHours || HasPowerCycles ||
        HasLifetimeWrites || HasErrorCounts || HasSmartAttributes;

    // Realtime Telemetry (реальные данные из Win32_PerfFormattedData_PerfDisk_PhysicalDisk)
    public double CurrentReadSpeedMBps { get; set; }
    public double CurrentWriteSpeedMBps { get; set; }
    public double CurrentIops { get; set; }
    public double CurrentLatencyMs { get; set; }
    public double CurrentQueueDepth { get; set; }
    public double ActiveTimePercent { get; set; }

    public List<StoragePartition> Partitions { get; set; } = new();
    public List<SmartAttribute> SmartAttributes { get; set; } = new();
    public StorageScore Score { get; set; } = new();

    // ── Форматирование с учётом доступности ──────────────────────────────────

    /// <summary>Температура или честное «Нет данных».</summary>
    public string TemperatureFormatted => HasTemperature ? $"{TemperatureC:F0} °C" : "Нет данных";

    /// <summary>
    /// Компактная надпись для карточки диска. Отдельное свойство, а не обрезанный
    /// TemperatureFormatted, потому что в карточке формат «н/д» короче и читается
    /// как «измерение отсутствует», а не как «ноль градусов».
    /// </summary>
    public string TemperatureDisplay => HasTemperature ? $"{TemperatureC:F0}°C" : "°C н/д";

    /// <summary>Откуда взята температура — показывается в подсказке, чтобы было видно источник.</summary>
    public string TemperatureSourceDescription => HasTemperature
        ? $"Источник: {Source}." + (string.IsNullOrEmpty(TelemetryNote) ? "" : " " + TelemetryNote)
        : (string.IsNullOrEmpty(TelemetryNote)
            ? "Температура недоступна: контроллер диска не публикует данные датчика."
            : "Температура недоступна. " + TelemetryNote);

    /// <summary>Ресурс или честное «Нет данных».</summary>
    public string WearFormatted =>
        HasWear ? $"{LifetimeRemainingPercent:F0}% (износ {WearLevelPercent:F0}%)" : "Нет данных";

    /// <summary>Наработка или честное «Нет данных».</summary>
    public string PowerOnHoursFormatted => HasPowerOnHours ? $"{PowerOnHours:N0} часов" : "Нет данных";

    /// <summary>Циклы включения или честное «Нет данных».</summary>
    public string PowerCyclesFormatted => HasPowerCycles ? $"{PowerCycles:N0} включений" : "Нет данных";

    /// <summary>Объём записи или честное «Нет данных».</summary>
    public string TotalWrittenFormatted => HasLifetimeWrites ? $"Записано: {Helpers.Formatters.Bytes(TotalBytesWritten)}" : "Данные недоступны";

    /// <summary>Фрагментация или честное «Нет данных».</summary>
    public string FragmentationFormatted => HasFragmentation ? $"{FragmentationPercent:F1}%" : "Нет данных";

    /// <summary>Состояние здоровья или честное «Неизвестно».</summary>
    public string HealthStatusFormatted =>
        string.IsNullOrWhiteSpace(HealthStatus) ? "Неизвестно" : HealthStatus;

    public string TotalSizeFormatted => Helpers.Formatters.Bytes(TotalSizeBytes);

    public string FreeSpaceFormatted
    {
        get
        {
            long free = 0;
            foreach (var p in Partitions) free += p.FreeSpaceBytes;
            return Helpers.Formatters.Bytes(free);
        }
    }

    public double TotalFreeBytes
    {
        get
        {
            long free = 0;
            foreach (var p in Partitions) free += p.FreeSpaceBytes;
            return free;
        }
    }

    public double FreeSpacePercent => TotalSizeBytes > 0 ? Math.Round(TotalFreeBytes / TotalSizeBytes * 100.0, 1) : 0;
    public double UsedSpacePercent => Math.Max(0, 100.0 - FreeSpacePercent);

    public string MediaTypeString => MediaType switch
    {
        StoragePhysicalMedia.NVMeSSD => "NVMe PCIe SSD",
        StoragePhysicalMedia.SataSSD => "SATA SSD",
        StoragePhysicalMedia.HDD => "HDD (Шпиндель)",
        StoragePhysicalMedia.USBFlash => "USB Накопитель",
        StoragePhysicalMedia.VirtualDisk => "Виртуальный диск",
        _ => "Тип не определён"
    };

    public string BusTypeString => BusType switch
    {
        StoragePhysicalBus.NVMe => "PCIe NVMe",
        StoragePhysicalBus.SATA => "SATA",
        StoragePhysicalBus.USB => "USB",
        StoragePhysicalBus.SAS => "SAS",
        StoragePhysicalBus.RAID => "RAID",
        StoragePhysicalBus.Virtual => "Виртуальный",
        _ => "Шина не определена"
    };

    public string IconGlyph => MediaType switch
    {
        StoragePhysicalMedia.NVMeSSD => "⚡",
        StoragePhysicalMedia.SataSSD => "🚀",
        StoragePhysicalMedia.HDD => "🖴",
        StoragePhysicalMedia.USBFlash => "💾",
        _ => "❔"
    };

    // Цвет и статус температуры применяются ТОЛЬКО к реально измеренному значению.
    public string TemperatureColor => !HasTemperature ? "#6B7280"
        : TemperatureC switch
        {
            >= 70 => "#EF4444",
            >= 55 => "#F59E0B",
            _ => "#10B981"
        };

    public string TemperatureStatus => !HasTemperature ? "Температура не измерена"
        : TemperatureC switch
        {
            >= 70 => "Критический перегрев (Троттлинг)",
            >= 55 => "Повышенная температура",
            _ => "Оптимальная температура"
        };

    public string HealthColor => HealthStatus.ToLowerInvariant() switch
    {
        "healthy" or "ok" or "good" => "#10B981",
        "warning" or "caution" or "degraded" => "#F59E0B",
        "unhealthy" or "failed" or "critical" => "#EF4444",
        _ => "#6B7280"   // нет данных — не рисуем «здоровье», а неопределённость
    };
}
