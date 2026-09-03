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
    public double TemperatureC { get; set; } = 35.0;
    public int HealthPercent { get; set; } = 100;
    public string HealthGrade { get; set; } = "A+";
    public long TotalBytesWritten { get; set; }
    public long PowerOnHours { get; set; }
    public bool IsSystemDisk { get; set; }
    public bool IsRemovable { get; set; }
    public List<PartitionInfo> Partitions { get; set; } = new();

    public string FormattedSize => FormatBytes(SizeBytes);

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
    public string RawValue { get; set; } = "";
    public string Status { get; set; } = "OK";
}

public sealed class SmartReport
{
    public int DiskIndex { get; set; }
    public string Model { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public int HealthPercent { get; set; } = 100;
    public string Grade { get; set; } = "A+";
    public double TemperatureC { get; set; } = 35.0;
    public List<SmartAttributeItem> Attributes { get; set; } = new();
    public List<string> Recommendations { get; set; } = new();
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
