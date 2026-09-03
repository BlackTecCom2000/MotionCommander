using System.IO;
using System.Management;
using Win11CopyDialog.Modules.StorageControlCenter.Models;

namespace Win11CopyDialog.Modules.StorageControlCenter.Services;

public static class StorageDiscoveryService
{
    private static readonly object _lock = new();
    private static List<StorageDisk>? _cachedDisks;
    private static DateTime _lastScanTime = DateTime.MinValue;

    public static List<StorageDisk> GetAllDisks(bool forceRefresh = false)
    {
        lock (_lock)
        {
            if (!forceRefresh && _cachedDisks != null && (DateTime.Now - _lastScanTime).TotalSeconds < 5)
            {
                return _cachedDisks;
            }

            var disks = new List<StorageDisk>();

            try
            {
                // Попытка 1: Современный Windows Storage Management API (MSFT_Disk & MSFT_Partition)
                disks = QueryStorageNamespace();
            }
            catch
            {
                // Попытка 2: Fallback на WMI Win32_DiskDrive и DriveInfo
                disks = QueryWmiFallback();
            }

            if (disks.Count == 0)
            {
                disks = QueryWmiFallback();
            }

            // Дополняем данные SMART, здоровьем и Storage Score
            foreach (var d in disks)
            {
                SmartHealthService.EnrichDiskHealth(d);
                StorageAdvisorService.EvaluateScore(d);
            }

            _cachedDisks = disks;
            _lastScanTime = DateTime.Now;
            return disks;
        }
    }

    private static List<StorageDisk> QueryStorageNamespace()
    {
        var disks = new List<StorageDisk>();
        var scope = new ManagementScope(@"\\.\root\microsoft\windows\storage");
        scope.Connect();

        // 1. Опрос физических накопителей MSFT_Disk
        var diskDict = new Dictionary<int, StorageDisk>();
        using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Number, FriendlyName, SerialNumber, BusType, PartitionStyle, Size, AllocatedSize, HealthStatus, OperationalStatus FROM MSFT_Disk")))
        using (var coll = searcher.Get())
        {
            foreach (ManagementObject obj in coll)
            {
                int number = Convert.ToInt32(obj["Number"] ?? -1);
                if (number < 0) continue;

                string name = obj["FriendlyName"]?.ToString() ?? $"Диск {number}";
                string serial = obj["SerialNumber"]?.ToString() ?? "";
                int busTypeVal = Convert.ToInt32(obj["BusType"] ?? 0);
                int partStyleVal = Convert.ToInt32(obj["PartitionStyle"] ?? 2);
                long totalSize = Convert.ToInt64(obj["Size"] ?? 0);
                long allocatedSize = Convert.ToInt64(obj["AllocatedSize"] ?? 0);

                var disk = new StorageDisk
                {
                    DiskNumber = number,
                    Model = name,
                    SerialNumber = serial,
                    TotalSizeBytes = totalSize,
                    AllocatedSizeBytes = allocatedSize > 0 ? allocatedSize : totalSize,
                    PartitionStyle = partStyleVal == 1 ? "MBR" : "GPT"
                };

                // Определение BusType
                disk.BusType = busTypeVal switch
                {
                    17 => StoragePhysicalBus.NVMe,
                    11 => StoragePhysicalBus.SATA,
                    7 => StoragePhysicalBus.USB,
                    10 => StoragePhysicalBus.SAS,
                    14 => StoragePhysicalBus.Virtual,
                    _ => StoragePhysicalBus.Unknown
                };

                // Определение MediaType по имени и шине
                string nameUpper = name.ToUpperInvariant();
                if (disk.BusType == StoragePhysicalBus.NVMe || nameUpper.Contains("NVME") || nameUpper.Contains("PCIE") || nameUpper.Contains("SN730") || nameUpper.Contains("EVO") || nameUpper.Contains("PRO"))
                {
                    disk.MediaType = StoragePhysicalMedia.NVMeSSD;
                    if (disk.BusType == StoragePhysicalBus.Unknown) disk.BusType = StoragePhysicalBus.NVMe;
                }
                else if (disk.BusType == StoragePhysicalBus.USB || nameUpper.Contains("USB") || nameUpper.Contains("FLASH"))
                {
                    disk.MediaType = StoragePhysicalMedia.USBFlash;
                }
                else if (nameUpper.Contains("SSD") || nameUpper.Contains("LEXAR") || nameUpper.Contains("KINGSTON") || nameUpper.Contains("SAMSUNG SSD"))
                {
                    disk.MediaType = StoragePhysicalMedia.SataSSD;
                }
                else
                {
                    disk.MediaType = StoragePhysicalMedia.HDD;
                }

                diskDict[number] = disk;
                disks.Add(disk);
            }
        }

        // 2. Опрос MSFT_PhysicalDisk для уточнения MediaType
        try
        {
            using var searcherPhys = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DeviceId, MediaType, SpindleSpeed FROM MSFT_PhysicalDisk"));
            using var collPhys = searcherPhys.Get();
            foreach (ManagementObject obj in collPhys)
            {
                if (int.TryParse(obj["DeviceId"]?.ToString(), out int devId) && diskDict.TryGetValue(devId, out var d))
                {
                    int mType = Convert.ToInt32(obj["MediaType"] ?? 0);
                    if (mType == 4 && d.MediaType == StoragePhysicalMedia.HDD)
                        d.MediaType = d.BusType == StoragePhysicalBus.NVMe ? StoragePhysicalMedia.NVMeSSD : StoragePhysicalMedia.SataSSD;
                    else if (mType == 3)
                        d.MediaType = StoragePhysicalMedia.HDD;
                }
            }
        }
        catch { }

        // 3. Опрос томов MSFT_Volume (для свободных объемов и меток)
        var volumeDict = new Dictionary<string, (string label, string fs, long freeBytes)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcherVol = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DriveLetter, FileSystemLabel, FileSystem, SizeRemaining FROM MSFT_Volume"));
            using var collVol = searcherVol.Get();
            foreach (ManagementObject obj in collVol)
            {
                string letter = obj["DriveLetter"]?.ToString() ?? "";
                if (string.IsNullOrEmpty(letter)) continue;

                string label = obj["FileSystemLabel"]?.ToString() ?? "";
                string fs = obj["FileSystem"]?.ToString() ?? "NTFS";
                long free = Convert.ToInt64(obj["SizeRemaining"] ?? 0);
                volumeDict[letter] = (label, fs, free);
            }
        }
        catch { }

        // 4. Опрос разделов MSFT_Partition
        using (var searcherPart = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DiskNumber, PartitionNumber, DriveLetter, Size, GptType, IsSystem, IsBoot FROM MSFT_Partition")))
        using (var collPart = searcherPart.Get())
        {
            foreach (ManagementObject obj in collPart)
            {
                int diskNum = Convert.ToInt32(obj["DiskNumber"] ?? -1);
                if (diskNum < 0 || !diskDict.TryGetValue(diskNum, out var targetDisk)) continue;

                int partNum = Convert.ToInt32(obj["PartitionNumber"] ?? 0);
                string letter = obj["DriveLetter"]?.ToString() ?? "";
                long size = Convert.ToInt64(obj["Size"] ?? 0);
                string gptType = obj["GptType"]?.ToString() ?? "";
                bool isSys = Convert.ToBoolean(obj["IsSystem"] ?? false);
                bool isBoot = Convert.ToBoolean(obj["IsBoot"] ?? false);

                var part = new StoragePartition
                {
                    DiskNumber = diskNum,
                    PartitionNumber = partNum,
                    DriveLetter = letter,
                    SizeBytes = size,
                    GptType = gptType,
                    IsSystem = isSys,
                    IsBoot = isBoot
                };

                if (isSys || isBoot) targetDisk.IsSystemDisk = true;

                if (!string.IsNullOrEmpty(letter) && volumeDict.TryGetValue(letter, out var volInfo))
                {
                    part.VolumeLabel = volInfo.label;
                    part.FileSystem = volInfo.fs;
                    part.FreeSpaceBytes = volInfo.freeBytes;
                }
                else if (!string.IsNullOrEmpty(letter))
                {
                    try
                    {
                        var dInfo = new DriveInfo(letter);
                        if (dInfo.IsReady)
                        {
                            part.VolumeLabel = dInfo.VolumeLabel;
                            part.FileSystem = dInfo.DriveFormat;
                            part.FreeSpaceBytes = dInfo.AvailableFreeSpace;
                        }
                    }
                    catch { }
                }

                targetDisk.Partitions.Add(part);
            }
        }

        // 5. Расчет нераспределенного пространства (Unallocated Space)
        foreach (var d in disks)
        {
            long allocated = 0;
            foreach (var p in d.Partitions) allocated += p.SizeBytes;
            d.AllocatedSizeBytes = allocated;

            long unallocated = d.TotalSizeBytes - allocated;
            if (unallocated > 50 * 1024 * 1024) // > 50 MB
            {
                d.Partitions.Add(new StoragePartition
                {
                    DiskNumber = d.DiskNumber,
                    PartitionNumber = d.Partitions.Count + 1,
                    SizeBytes = unallocated,
                    IsAllocated = false,
                    FileSystem = "Unallocated"
                });
            }
        }

        return disks;
    }

    private static List<StorageDisk> QueryWmiFallback()
    {
        var disks = new List<StorageDisk>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Index, Caption, Model, InterfaceType, Size, Status FROM Win32_DiskDrive");
            using var coll = searcher.Get();

            foreach (ManagementObject obj in coll)
            {
                int index = Convert.ToInt32(obj["Index"] ?? 0);
                string model = obj["Model"]?.ToString() ?? obj["Caption"]?.ToString() ?? $"Диск {index}";
                long size = Convert.ToInt64(obj["Size"] ?? 0);
                string ifType = obj["InterfaceType"]?.ToString() ?? "";

                var disk = new StorageDisk
                {
                    DiskNumber = index,
                    Model = model,
                    TotalSizeBytes = size,
                    AllocatedSizeBytes = size,
                    BusType = ifType.ToUpperInvariant().Contains("SCSI") ? StoragePhysicalBus.NVMe : StoragePhysicalBus.SATA
                };

                string modelUpper = model.ToUpperInvariant();
                if (modelUpper.Contains("NVME") || modelUpper.Contains("PCIE") || modelUpper.Contains("SN730"))
                    disk.MediaType = StoragePhysicalMedia.NVMeSSD;
                else if (modelUpper.Contains("SSD"))
                    disk.MediaType = StoragePhysicalMedia.SataSSD;
                else if (modelUpper.Contains("USB"))
                    disk.MediaType = StoragePhysicalMedia.USBFlash;
                else
                    disk.MediaType = StoragePhysicalMedia.HDD;

                disks.Add(disk);
            }
        }
        catch { }

        // Добавляем DriveInfo в первый найденный диск
        if (disks.Count > 0)
        {
            foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
            {
                disks[0].Partitions.Add(new StoragePartition
                {
                    DiskNumber = disks[0].DiskNumber,
                    DriveLetter = d.Name.TrimEnd('\\', ':'),
                    VolumeLabel = d.VolumeLabel,
                    FileSystem = d.DriveFormat,
                    SizeBytes = d.TotalSize,
                    FreeSpaceBytes = d.AvailableFreeSpace
                });
            }
        }

        return disks;
    }
}
