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

            // Дополняем данные SMART и здоровьем накопителя.
            // Всё берётся из реальных измерений; недоступное остаётся
            // помеченным как «Нет данных», а не заполняется константами.
            foreach (var d in disks)
            {
                SmartHealthService.EnrichDiskHealth(d);
            }

            // Реальная фрагментация через defrag /A.
            // Раньше AnalyzeFragmentationAsync вообще не вызывался, а вместо
            // него в модель писалась константа 4.8% (HDD) / 0.5% (SSD).
            AnalyzeRealFragmentation(disks);

            // Фрагментация не влияет на сам балл, но влияет на рекомендации,
            // поэтому пересчитываем оценку после того, как она измерена.
            foreach (var d in disks)
            {
                StorageAdvisorService.EvaluateScore(d);
            }

            _cachedDisks = disks;
            _lastScanTime = DateTime.Now;
            return disks;
        }
    }

    /// <summary>
    /// Измеряет реальную фрагментацию через системный дефрагментатор.
    ///
    /// <para>Раньше фрагментация была константой: 4.8% для HDD и 0.5% для SSD
    /// (SmartHealthService.CheckTrimAndAlignment), при том что готовый
    /// анализатор <c>DiskOptimizerService.AnalyzeFragmentationAsync</c> был
    /// написан, но не вызывался ни разу.</para>
    ///
    /// <para>Измерение выполняется только для томов с буквой диска и только
    /// для HDD. Если дефрагментатор не ответил — флаг HasFragmentation
    /// остаётся снятым, и интерфейс показывает «Нет данных».</para>
    /// </summary>
    private static void AnalyzeRealFragmentation(List<StorageDisk> disks)
    {
        foreach (var disk in disks)
        {
            // Для SSD фрагментация не имеет практического значения,
            // а defrag /A на SSD всё равно ничего полезного не сообщает.
            if (disk.MediaType != StoragePhysicalMedia.HDD) continue;

            foreach (var part in disk.Partitions)
            {
                if (string.IsNullOrEmpty(part.DriveLetter)) continue;

                try
                {
                    double? pct = DiskOptimizerService
                        .AnalyzeFragmentationAsync(part.DriveLetter)
                        .GetAwaiter().GetResult();

                    if (pct.HasValue)
                    {
                        disk.FragmentationPercent = pct.Value;
                        disk.HasFragmentation = true;
                        break;   // достаточно одного тома на диск
                    }
                }
                catch
                {
                    // Анализ не удался — оставляем «Нет данных», а не 0%.
                }
            }
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
                    PartitionStyle = partStyleVal switch
                    {
                        0 => "MBR",
                        2 => "GPT",
                        _ => "Неизвестно"
                    },

                    // Раньше эти два поля НИКОГДА не заполнялись, а в модели
                    // стояли значения по умолчанию "Healthy" и "Online".
                    // Теперь берём их из уже выбранных колонок WMI.
                    HealthStatus = obj["HealthStatus"]?.ToString() ?? "",
                    OperationalStatus = obj["OperationalStatus"]?.ToString() ?? "",
                };

                // Определение BusType по числовому коду MSFT_Disk.BusType
                disk.BusType = busTypeVal switch
                {
                    17 => StoragePhysicalBus.NVMe,
                    11 => StoragePhysicalBus.SATA,
                    7 => StoragePhysicalBus.USB,
                    10 => StoragePhysicalBus.SAS,
                    14 => StoragePhysicalBus.Virtual,
                    _ => StoragePhysicalBus.Unknown
                };

                // Тип носителя по шине. Никаких догадок по подстрокам в имени
                // модели: раньше проверка nameUpper.Contains("PRO") относила
                // к NVMe любой диск с «PRO» в названии (например «990 PRO» на
                // SATA или USB-флешку «ProDrive»), а также подменяла шину на NVMe.
                // Уточнение по фактическому типу делается ниже из MSFT_PhysicalDisk.
                disk.MediaType = disk.BusType switch
                {
                    StoragePhysicalBus.NVMe => StoragePhysicalMedia.NVMeSSD,
                    StoragePhysicalBus.USB => StoragePhysicalMedia.USBFlash,
                    StoragePhysicalBus.Virtual => StoragePhysicalMedia.VirtualDisk,
                    _ => StoragePhysicalMedia.Unknown
                };

                diskDict[number] = disk;
                disks.Add(disk);
            }
        }

        // 2. Опрос MSFT_PhysicalDisk для уточнения MediaType по реальным признакам.
        //    MSFT_Disk не сообщает тип носителя, поэтому берём его здесь:
        //    SpindleSpeed > 0 — вращающийся HDD, 0 — твердотельный.
        try
        {
            using var searcherPhys = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT DeviceId, MediaType, BusType, SpindleSpeed FROM MSFT_PhysicalDisk"));
            using var collPhys = searcherPhys.Get();
            foreach (ManagementObject obj in collPhys)
            {
                using (obj)
                {
                    if (!int.TryParse(obj["DeviceId"]?.ToString(), out int devId) ||
                        !diskDict.TryGetValue(devId, out var d))
                    {
                        continue;
                    }

                    int spindle = 0;
                    try { spindle = Convert.ToInt32(obj["SpindleSpeed"] ?? 0); } catch { }

                    int physMediaType = 0;
                    try { physMediaType = Convert.ToInt32(obj["MediaType"] ?? 0); } catch { }

                    // MSFT_PhysicalDisk.BusType — тот же код, что у MSFT_Disk.
                    int physBus = 0;
                    try { physBus = Convert.ToInt32(obj["BusType"] ?? 0); } catch { }

                    if (physBus != 0)
                    {
                        d.BusType = physBus switch
                        {
                            17 => StoragePhysicalBus.NVMe,
                            11 => StoragePhysicalBus.SATA,
                            7 => StoragePhysicalBus.USB,
                            10 => StoragePhysicalBus.SAS,
                            14 => StoragePhysicalBus.Virtual,
                            _ => StoragePhysicalBus.Unknown
                        };
                    }

                    // Приоритет реальных признаков:
                    // 1) MediaType == 3 (HDD) или SpindleSpeed > 0  → вращающийся диск
                    // 2) BusType == NVMe                                → NVMe SSD
                    // 3) BusType == USB                                → флешка
                    // 4) MediaType == 4 (SSD) без шпинделя             → SATA SSD
                    if (physMediaType == 3 || spindle > 0)
                    {
                        d.MediaType = StoragePhysicalMedia.HDD;
                    }
                    else if (d.BusType == StoragePhysicalBus.NVMe)
                    {
                        d.MediaType = StoragePhysicalMedia.NVMeSSD;
                    }
                    else if (d.BusType == StoragePhysicalBus.USB)
                    {
                        d.MediaType = StoragePhysicalMedia.USBFlash;
                    }
                    else if (physMediaType == 4)
                    {
                        d.MediaType = StoragePhysicalMedia.SataSSD;
                    }
                }
            }
        }
        catch { }

        // 2b. Если MSFT_PhysicalDisk не дал тип — оставляем Unknown, а не гадаем.

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
            using var searcher = new ManagementObjectSearcher(
                "SELECT Index, Caption, Model, InterfaceType, Size, Status, SerialNumber, MediaType, PNPDeviceID FROM Win32_DiskDrive");
            using var coll = searcher.Get();

            foreach (ManagementObject obj in coll)
            {
                using (obj)
                {
                    int index = Convert.ToInt32(obj["Index"] ?? 0);
                    string model = obj["Model"]?.ToString() ?? obj["Caption"]?.ToString() ?? $"Диск {index}";
                    long size = Convert.ToInt64(obj["Size"] ?? 0);
                    string ifType = obj["InterfaceType"]?.ToString() ?? "";

                    // Раньше SerialNumber не заполнялся вовсе и оставался пустым.
                    string serial = obj["SerialNumber"]?.ToString()?.Trim() ?? "";
                    if (serial.StartsWith("FALLBACK-", StringComparison.OrdinalIgnoreCase))
                        serial = "";

                    // Win32_DiskDrive.Status — реальное состояние диска.
                    // Раньше поле выбиралось, но не читалось.
                    string status = obj["Status"]?.ToString()?.Trim() ?? "";

                    var disk = new StorageDisk
                    {
                        DiskNumber = index,
                        Model = model,
                        SerialNumber = serial,
                        TotalSizeBytes = size,
                        AllocatedSizeBytes = size,
                        HealthStatus = string.IsNullOrEmpty(status) ? "" : status,
                        OperationalStatus = "",

                        // Раньше здесь стояло:
                        //   BusType = ifType.Contains("SCSI") ? NVMe : SATA
                        // то есть ЛЮБОЙ диск с InterfaceType="SCSI" (а это почти
                        // все NVMe через SCSI miniport, но также RAID и USB-хабы)
                        // помечался как NVMe, а всё остальное — как SATA.
                        // Теперь по умолчанию шина неизвестна и уточняется ниже.
                        BusType = StoragePhysicalBus.Unknown,
                        MediaType = StoragePhysicalMedia.Unknown,

                        // Этот путь — запасной, когда WMI-хранилище накопителей
                        // недоступно. Признак честности: данные минимальны.
                        TelemetryNote = "WMI-хранилище накопителей недоступно, показано базовое описание из Win32_DiskDrive."
                    };

                    // Win32_DiskDrive.MediaType: 3 = Fixed hard disk media.
                    int mediaType = 0;
                    try { mediaType = Convert.ToInt32(obj["MediaType"] ?? 0); } catch { }

                    string modelUpper = model.ToUpperInvariant();
                    string pnp = obj["PNPDeviceID"]?.ToString()?.ToUpperInvariant() ?? "";

                    if (pnp.StartsWith(@"\\HOSTSTORAGEPORT") || ifType.Equals("SCSI", StringComparison.OrdinalIgnoreCase)
                        && modelUpper.Contains("NVME"))
                    {
                        disk.BusType = StoragePhysicalBus.NVMe;
                        disk.MediaType = StoragePhysicalMedia.NVMeSSD;
                    }
                    else if (pnp.StartsWith(@"\\USBSTOR") || ifType.Equals("USB", StringComparison.OrdinalIgnoreCase))
                    {
                        disk.BusType = StoragePhysicalBus.USB;
                        disk.MediaType = StoragePhysicalMedia.USBFlash;
                    }
                    else if (modelUpper.Contains("NVME"))
                    {
                        disk.BusType = StoragePhysicalBus.NVMe;
                        disk.MediaType = StoragePhysicalMedia.NVMeSSD;
                    }
                    else if (modelUpper.Contains("SSD") || mediaType == 4)
                    {
                        disk.BusType = StoragePhysicalBus.SATA;
                        disk.MediaType = StoragePhysicalMedia.SataSSD;
                    }
                    else if (mediaType == 3)
                    {
                        disk.BusType = StoragePhysicalBus.SATA;
                        disk.MediaType = StoragePhysicalMedia.HDD;
                    }
                    // иначе остаётся Unknown — лучше «неизвестно», чем выдумка

                    disks.Add(disk);
                }
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
