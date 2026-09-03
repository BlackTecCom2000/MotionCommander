using Win11CopyDialog.Helpers;

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

public sealed class StorageDisk
{
    public int DiskNumber { get; set; }
    public string Model { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public StoragePhysicalBus BusType { get; set; } = StoragePhysicalBus.Unknown;
    public StoragePhysicalMedia MediaType { get; set; } = StoragePhysicalMedia.Unknown;
    public string PartitionStyle { get; set; } = "GPT"; // GPT or MBR
    
    public long TotalSizeBytes { get; set; }
    public long AllocatedSizeBytes { get; set; }
    public long UnallocatedSizeBytes => Math.Max(0, TotalSizeBytes - AllocatedSizeBytes);
    
    public string HealthStatus { get; set; } = "Healthy";
    public string OperationalStatus { get; set; } = "Online";
    public double TemperatureC { get; set; } = 35.0;
    public double WearLevelPercent { get; set; } = 0.0; // 0 = new, 100 = worn out
    public double LifetimeRemainingPercent => Math.Max(0, 100.0 - WearLevelPercent);
    
    public long PowerOnHours { get; set; }
    public long PowerCycles { get; set; }
    public long UnsafeShutdowns { get; set; }
    public long TotalBytesWritten { get; set; }
    public long TotalBytesRead { get; set; }
    
    public bool IsSystemDisk { get; set; }
    public bool IsTrimSupported { get; set; } = true;
    public bool IsTrimEnabled { get; set; } = true;
    public bool Is4KAligned { get; set; } = true;
    public double FragmentationPercent { get; set; } = 0.0;
    
    // Realtime Telemetry
    public double CurrentReadSpeedMBps { get; set; }
    public double CurrentWriteSpeedMBps { get; set; }
    public double CurrentIops { get; set; }
    public double CurrentLatencyMs { get; set; }
    public double CurrentQueueDepth { get; set; }
    public double ActiveTimePercent { get; set; }
    
    // Partitions & SMART
    public List<StoragePartition> Partitions { get; set; } = new();
    public List<SmartAttribute> SmartAttributes { get; set; } = new();
    public StorageScore Score { get; set; } = new();

    // Helpers
    public string TotalSizeFormatted => Formatters.Bytes(TotalSizeBytes);
    public string FreeSpaceFormatted
    {
        get
        {
            long free = 0;
            foreach (var p in Partitions) free += p.FreeSpaceBytes;
            return Formatters.Bytes(free);
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
        _ => "Накопитель"
    };

    public string BusTypeString => BusType switch
    {
        StoragePhysicalBus.NVMe => "PCIe NVMe",
        StoragePhysicalBus.SATA => "SATA III 6Gb/s",
        StoragePhysicalBus.USB => "USB 3.0/3.2",
        StoragePhysicalBus.SAS => "SAS",
        _ => "Standard I/O"
    };

    public string IconGlyph => MediaType switch
    {
        StoragePhysicalMedia.NVMeSSD => "⚡",
        StoragePhysicalMedia.SataSSD => "🚀",
        StoragePhysicalMedia.HDD => "🖴",
        StoragePhysicalMedia.USBFlash => "💾",
        _ => "🖴"
    };

    public string TemperatureColor => TemperatureC switch
    {
        >= 70 => "#EF4444", // Overheating Red
        >= 55 => "#F59E0B", // Warning Amber
        _ => "#10B981"      // Normal Green
    };

    public string TemperatureStatus => TemperatureC switch
    {
        >= 70 => "Критический перегрев (Троттлинг)",
        >= 55 => "Повышенная температура",
        _ => "Оптимальная температура"
    };

    public string HealthColor => HealthStatus.ToLowerInvariant() switch
    {
        "healthy" or "ok" or "good" => "#10B981",
        "warning" or "caution" => "#F59E0B",
        _ => "#EF4444"
    };
}
