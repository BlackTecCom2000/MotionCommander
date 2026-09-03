using Win11CopyDialog.Helpers;

namespace Win11CopyDialog.Modules.StorageControlCenter.Models;

public enum PartitionTypeCategory
{
    BasicData,
    SystemEfi,
    MicrosoftReserved,
    Recovery,
    Unallocated,
    Unknown
}

public sealed class StoragePartition
{
    public int DiskNumber { get; set; }
    public int PartitionNumber { get; set; }
    public string DriveLetter { get; set; } = "";
    public string VolumeLabel { get; set; } = "";
    public string FileSystem { get; set; } = "";
    public long SizeBytes { get; set; }
    public long FreeSpaceBytes { get; set; }
    public long UsedSpaceBytes => Math.Max(0, SizeBytes - FreeSpaceBytes);
    public double UsedPercent => SizeBytes > 0 ? Math.Round((double)UsedSpaceBytes / SizeBytes * 100.0, 1) : 0;
    
    public bool IsSystem { get; set; }
    public bool IsBoot { get; set; }
    public bool IsReadOnly { get; set; }
    public bool IsBitLockerEncrypted { get; set; }
    public bool IsAllocated { get; set; } = true;
    public string GptType { get; set; } = "";
    
    public PartitionTypeCategory Category
    {
        get
        {
            if (!IsAllocated) return PartitionTypeCategory.Unallocated;
            if (IsSystem || GptType.Equals("{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}", StringComparison.OrdinalIgnoreCase))
                return PartitionTypeCategory.SystemEfi;
            if (GptType.Equals("{e3c9e316-0b5c-4db8-817d-f92df00215ae}", StringComparison.OrdinalIgnoreCase))
                return PartitionTypeCategory.MicrosoftReserved;
            if (GptType.Equals("{de94bba4-06d1-4d40-a16a-bfd50179d6ac}", StringComparison.OrdinalIgnoreCase))
                return PartitionTypeCategory.Recovery;
            return PartitionTypeCategory.BasicData;
        }
    }

    public string DisplayName
    {
        get
        {
            if (!IsAllocated) return "Нераспределенное пространство";
            if (!string.IsNullOrEmpty(DriveLetter))
                return $"{DriveLetter}: {(string.IsNullOrEmpty(VolumeLabel) ? "Локальный диск" : VolumeLabel)}";
            if (Category == PartitionTypeCategory.SystemEfi) return "EFI Системный раздел";
            if (Category == PartitionTypeCategory.MicrosoftReserved) return "MSR (Зарезервировано)";
            if (Category == PartitionTypeCategory.Recovery) return "Раздел восстановления";
            return $"Раздел {PartitionNumber}";
        }
    }

    public string SizeFormatted => Formatters.Bytes(SizeBytes);
    public string FreeSpaceFormatted => Formatters.Bytes(FreeSpaceBytes);
    public string UsedSpaceFormatted => Formatters.Bytes(UsedSpaceBytes);
    
    // Proportional width weight for the partition visualizer
    public double MapWeight => Math.Max(0.05, (double)SizeBytes / (1024.0 * 1024.0 * 1024.0));
}
