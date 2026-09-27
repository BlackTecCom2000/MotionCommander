namespace MotionCommander.Core.Models;

public enum DiskBusType
{
    NVMe,
    SATA,
    SCSI,
    SAS,
    USB,
    Virtual,
    Unknown
}

public enum DiskMediaType
{
    SSD,
    HDD,
    NVMe,
    FlashMemory,
    Unknown
}

public sealed class StorageDiskInfo
{
    public int Index { get; set; }
    public string DeviceId { get; set; } = "";
    public string DevicePath { get; set; } = ""; // e.g. /dev/nvme0n1 or \\.\PhysicalDrive0 or /dev/disk0
    public string Model { get; set; } = "Generic Storage Device";
    public string SerialNumber { get; set; } = "";
    public long SizeBytes { get; set; }
    public DiskBusType BusType { get; set; } = DiskBusType.Unknown;
    public DiskMediaType MediaType { get; set; } = DiskMediaType.Unknown;

    /// <summary>
    /// Температура в градусах Цельсия. Актуальна только при
    /// <see cref="TemperatureMeasured"/> = true.
    /// </summary>
    public double TemperatureC { get; set; }

    /// <summary>Истина, если температура реально считана с контроллера.</summary>
    public bool TemperatureMeasured { get; set; }

    /// <summary>
    /// Здоровье в процентах. Актуально только при
    /// <see cref="HealthMeasured"/> = true.
    /// </summary>
    public int HealthPercent { get; set; }

    /// <summary>Истина, если состояние реально прочитано из системы.</summary>
    public bool HealthMeasured { get; set; }

    /// <summary>Износ в процентах. Актуально только при <see cref="WearMeasured"/>.</summary>
    public double WearLevelPercent { get; set; }

    /// <summary>Истина, если износ реально измерен.</summary>
    public bool WearMeasured { get; set; }

    public string HealthGrade { get; set; } = "н/д";
    public long TotalBytesWritten { get; set; }
    public long PowerOnHours { get; set; }

    /// <summary>Циклы включения питания. Актуально при заполнении реальными счётчиками.</summary>
    public long PowerCycles { get; set; }
    public bool IsSystemDisk { get; set; }
    public bool IsRemovable { get; set; }
    public List<PartitionInfo> Partitions { get; set; } = new();

    public string FormattedSize => FormatBytes(SizeBytes);

    /// <summary>Температура или честное «нет данных».</summary>
    public string TemperatureFormatted => TemperatureMeasured ? $"{TemperatureC:F0} °C" : "нет данных";

    /// <summary>Здоровье или честное «не измерено».</summary>
    public string HealthFormatted => HealthMeasured ? $"{HealthPercent}%" : "не измерено";

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "0 Б";
        string[] suffixes = { "Б", "КБ", "МБ", "ГБ", "ТБ", "ПБ" };
        int counter = 0;
        decimal number = bytes;
        while (Math.Round(number / 1024m) >= 1 && counter < suffixes.Length - 1)
        {
            number /= 1024m;
            counter++;
        }
        return $"{number:F1} {suffixes[counter]}";
    }
}

public sealed class PartitionInfo
{
    public int PartitionNumber { get; set; }
    public string DevicePath { get; set; } = ""; // /dev/nvme0n1p1 or C: or /dev/disk0s1
    public string MountPoint { get; set; } = "";  // e.g. / or /home or C:\ or /Volumes/Data
    public string VolumeLabel { get; set; } = "";
    public string FileSystem { get; set; } = "Unknown"; // ext4, btrfs, apfs, ntfs, fat32, zfs
    public long SizeBytes { get; set; }
    public long FreeBytes { get; set; }
    public bool IsBoot { get; set; }
    public bool IsSystem { get; set; }
    public bool IsReadOnly { get; set; }

    public double UsedPercent => SizeBytes > 0 ? (double)(SizeBytes - FreeBytes) / SizeBytes * 100.0 : 0;
    public string FormattedSize => StorageDiskInfo.FormatBytes(SizeBytes);
    public string FormattedFree => StorageDiskInfo.FormatBytes(FreeBytes);
}

public sealed class SmartAttributeItem
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string CurrentValue { get; set; } = "";
    public string WorstValue { get; set; } = "";
    public string Threshold { get; set; } = "";

    /// <summary>
    /// «Сырое» значение атрибута — целое число.
    ///
    /// <para>Раньше это свойство было строкой, что вынуждало подставлять
    /// текстовые заглушки. Теперь это число, а для атрибутов без числового
    /// показателя используется <see cref="HasRawValue"/> = false.</para>
    /// </summary>
    public long RawValue { get; set; }

    /// <summary>Истина, если значение реально прочитано с контроллера.</summary>
    public bool HasRawValue { get; set; }

    /// <summary>Значение для отображения: число либо честное «н/д».</summary>
    public string RawValueFormatted => HasRawValue ? RawValue.ToString() : "н/д";

    public string Status { get; set; } = "OK";
}

public sealed class SmartReport
{
    public int DiskIndex { get; set; }
    public string Model { get; set; } = "";
    public string SerialNumber { get; set; } = "";

    /// <summary>
    /// Оценка здоровья в процентах.
    ///
    /// <para>Раньше значение по умолчанию было 100, что означало «диск в идеале»
    /// даже при полном отсутствии данных. Теперь 0 вместе с
    /// <see cref="HealthMeasured"/> = false означает «не измерено».</para>
    /// </summary>
    public int HealthPercent { get; set; }

    /// <summary>Истина, если здоровье реально считано из системных данных.</summary>
    public bool HealthMeasured { get; set; }

    /// <summary>Истина, если температура реально измерена.</summary>
    public bool TemperatureMeasured { get; set; }

    public string Grade { get; set; } = "н/д";
    public double TemperatureC { get; set; }
    public List<SmartAttributeItem> Attributes { get; set; } = new();
    public List<string> Recommendations { get; set; } = new();

    /// <summary>Температура для отображения, либо честное «нет данных».</summary>
    public string TemperatureFormatted => TemperatureMeasured ? $"{TemperatureC:F0} °C" : "нет данных";

    /// <summary>Здоровье для отображения, либо честное «не измерено».</summary>
    public string HealthFormatted => HealthMeasured ? $"{HealthPercent}%" : "не измерено";
}

public sealed class SystemSnapshot
{
    public double CpuLoadPercent { get; set; }
    public long TotalRamBytes { get; set; }
    public long AvailableRamBytes { get; set; }
    public double RamUsedPercent => TotalRamBytes > 0 ? (double)(TotalRamBytes - AvailableRamBytes) / TotalRamBytes * 100.0 : 0;
    public string CpuModel { get; set; } = "";
    public int CoreCount { get; set; }
}
