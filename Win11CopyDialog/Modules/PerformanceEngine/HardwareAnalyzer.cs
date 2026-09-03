using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using Win11CopyDialog.Helpers;

namespace Win11CopyDialog.Modules.PerformanceEngine;

public enum StorageMediaType
{
    Unknown,
    HDD,
    SSD,
    NVMe,
    USB,
    RAMDisk
}

public sealed class DiskHardwareInfo
{
    public int DeviceId { get; set; }
    public string Model { get; set; } = "";
    public string BusType { get; set; } = "";
    public StorageMediaType MediaType { get; set; } = StorageMediaType.Unknown;
    public long SizeBytes { get; set; }
    public List<string> DriveLetters { get; set; } = new();

    public string SizeFormatted => Formatters.Bytes(SizeBytes);
    public string MediaTypeString => MediaType switch
    {
        StorageMediaType.NVMe => "NVMe PCIe SSD",
        StorageMediaType.SSD => "SATA SSD",
        StorageMediaType.HDD => "HDD (Шпиндель)",
        StorageMediaType.USB => "USB Накопитель",
        StorageMediaType.RAMDisk => "RAM Диск",
        _ => "Накопитель"
    };

    public string Icon => MediaType switch
    {
        StorageMediaType.NVMe => "⚡",
        StorageMediaType.SSD => "🚀",
        StorageMediaType.HDD => "🖴",
        StorageMediaType.USB => "💾",
        _ => "🖴"
    };
}

public sealed class TransferScenarioProfile
{
    public DiskHardwareInfo? SourceDisk { get; set; }
    public DiskHardwareInfo? DestinationDisk { get; set; }
    public bool IsSamePhysicalDisk { get; set; }
    public int RecommendedBufferSize { get; set; } = 1024 * 1024;
    public int RecommendedConcurrency { get; set; } = 4;
    public string StrategyName { get; set; } = "Стандартная";
    public string Description { get; set; } = "";
}

/// <summary>
/// Аппаратный анализатор: опрашивает свойства физических накопителей Windows,
/// определяет тип шины (NVMe PCIe / SATA / USB), тип носителя (SSD vs HDD),
/// сопоставляет логические буквы дисков с физическими накопителями и рассчитывает
/// оптимальные параметры для устранения bottleneck'ов.
/// </summary>
public static class HardwareAnalyzer
{
    private static readonly object _lock = new();
    private static List<DiskHardwareInfo>? _cachedDisks;
    private static DateTime _lastScanTime = DateTime.MinValue;

    public static int LogicalCoreCount => Environment.ProcessorCount;

    public static (long totalBytes, long freeBytes) GetSystemMemoryInfo()
    {
        try
        {
            var mem = new MEMORYSTATUSEX();
            mem.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            if (GlobalMemoryStatusEx(ref mem))
            {
                return ((long)mem.ullTotalPhys, (long)mem.ullAvailPhys);
            }
        }
        catch { }

        long total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return (total, total / 2);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    /// <summary>
    /// Получить список физических дисков и ассоциированных с ними букв разделов.
    /// </summary>
    public static List<DiskHardwareInfo> GetPhysicalDisks(bool forceRefresh = false)
    {
        lock (_lock)
        {
            if (!forceRefresh && _cachedDisks != null && (DateTime.Now - _lastScanTime).TotalMinutes < 5)
            {
                return _cachedDisks;
            }

            var disks = new List<DiskHardwareInfo>();

            try
            {
                // Запрос физических дисков через WMI / MSFT_PhysicalDisk
                using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT * FROM MSFT_PhysicalDisk");
                foreach (ManagementObject mo in searcher.Get())
                {
                    int devId = Convert.ToInt32(mo["DeviceId"] ?? 0);
                    string friendlyName = mo["FriendlyName"]?.ToString() ?? "";
                    string busTypeStr = mo["BusType"]?.ToString() ?? "";
                    int busType = 0;
                    int.TryParse(busTypeStr, out busType);
                    int mediaTypeInt = Convert.ToInt32(mo["MediaType"] ?? 0);
                    long size = Convert.ToInt64(mo["Size"] ?? 0);

                    var info = new DiskHardwareInfo
                    {
                        DeviceId = devId,
                        Model = friendlyName,
                        SizeBytes = size
                    };

                    // BusType: 17 = NVMe, 11 = SATA, 7 = USB, 8 = RAID, 3 = ATAPI
                    // MediaType: 4 = SSD, 3 = HDD, 0 = Unspecified
                    if (busType == 17 || friendlyName.Contains("NVMe", StringComparison.OrdinalIgnoreCase))
                    {
                        info.MediaType = StorageMediaType.NVMe;
                        info.BusType = "NVMe (PCIe)";
                    }
                    else if (busType == 7 || friendlyName.Contains("USB", StringComparison.OrdinalIgnoreCase))
                    {
                        info.MediaType = StorageMediaType.USB;
                        info.BusType = "USB";
                    }
                    else if (mediaTypeInt == 4 || friendlyName.Contains("SSD", StringComparison.OrdinalIgnoreCase))
                    {
                        info.MediaType = StorageMediaType.SSD;
                        info.BusType = "SATA SSD";
                    }
                    else if (mediaTypeInt == 3 || friendlyName.Contains("HDD", StringComparison.OrdinalIgnoreCase) || friendlyName.Contains("TOSHIBA", StringComparison.OrdinalIgnoreCase) || friendlyName.Contains("WDC", StringComparison.OrdinalIgnoreCase))
                    {
                        info.MediaType = StorageMediaType.HDD;
                        info.BusType = "SATA HDD";
                    }
                    else
                    {
                        info.MediaType = StorageMediaType.SSD; // Default modern assumption
                        info.BusType = "SATA";
                    }

                    disks.Add(info);
                }
            }
            catch
            {
                // Резервный сбор через Win32_DiskDrive
                try
                {
                    using var searcher2 = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDrive");
                    foreach (ManagementObject mo in searcher2.Get())
                    {
                        int devId = Convert.ToInt32(mo["Index"] ?? 0);
                        string model = mo["Model"]?.ToString() ?? "";
                        string ifType = mo["InterfaceType"]?.ToString() ?? "";
                        long size = Convert.ToInt64(mo["Size"] ?? 0);

                        var mType = StorageMediaType.HDD;
                        if (model.Contains("NVMe", StringComparison.OrdinalIgnoreCase)) mType = StorageMediaType.NVMe;
                        else if (model.Contains("SSD", StringComparison.OrdinalIgnoreCase)) mType = StorageMediaType.SSD;
                        else if (ifType.Equals("USB", StringComparison.OrdinalIgnoreCase)) mType = StorageMediaType.USB;

                        disks.Add(new DiskHardwareInfo
                        {
                            DeviceId = devId,
                            Model = model,
                            BusType = ifType,
                            MediaType = mType,
                            SizeBytes = size
                        });
                    }
                }
                catch { }
            }

            // Сопоставление букв дисков с физическими дисками
            MapDriveLettersToPhysicalDisks(disks);

            _cachedDisks = disks;
            _lastScanTime = DateTime.Now;
            return disks;
        }
    }

    private static void MapDriveLettersToPhysicalDisks(List<DiskHardwareInfo> disks)
    {
        try
        {
            using var partSearcher = new ManagementObjectSearcher("SELECT Antecedent, Dependent FROM Win32_LogicalDiskToPartition");
            foreach (ManagementObject rel in partSearcher.Get())
            {
                string antecedent = rel["Antecedent"]?.ToString() ?? ""; // Win32_DiskPartition.DeviceID="Disk #1, Partition #1"
                string dependent = rel["Dependent"]?.ToString() ?? "";   // Win32_LogicalDisk.DeviceID="C:"

                int diskIndex = -1;
                int hashIdx = antecedent.IndexOf("Disk #");
                if (hashIdx >= 0)
                {
                    int commaIdx = antecedent.IndexOf(',', hashIdx);
                    if (commaIdx > hashIdx)
                    {
                        string numStr = antecedent.Substring(hashIdx + 6, commaIdx - hashIdx - 6);
                        int.TryParse(numStr, out diskIndex);
                    }
                }

                string driveLetter = "";
                int devIdIdx = dependent.IndexOf("DeviceID=\"");
                if (devIdIdx >= 0)
                {
                    driveLetter = dependent.Substring(devIdIdx + 10, 2).ToUpperInvariant();
                }

                if (diskIndex >= 0 && !string.IsNullOrEmpty(driveLetter))
                {
                    var targetDisk = disks.FirstOrDefault(d => d.DeviceId == diskIndex);
                    if (targetDisk != null && !targetDisk.DriveLetters.Contains(driveLetter))
                    {
                        targetDisk.DriveLetters.Add(driveLetter);
                    }
                }
            }
        }
        catch { }
    }

    public static DiskHardwareInfo? GetDiskForPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        string root = Path.GetPathRoot(path)?.ToUpperInvariant().TrimEnd('\\') ?? "";
        if (string.IsNullOrEmpty(root)) return null;

        var disks = GetPhysicalDisks();
        return disks.FirstOrDefault(d => d.DriveLetters.Any(l => l.StartsWith(root)));
    }

    public static bool IsSamePhysicalDisk(string pathA, string pathB)
    {
        var diskA = GetDiskForPath(pathA);
        var diskB = GetDiskForPath(pathB);
        if (diskA == null || diskB == null) return false;
        return diskA.DeviceId == diskB.DeviceId;
    }

    /// <summary>
    /// Автоматический расчёт оптимальной стратегии передачи на основе свойств источника и приёмника.
    /// </summary>
    public static TransferScenarioProfile AnalyzeTransferScenario(string sourcePath, string destPath)
    {
        var diskA = GetDiskForPath(sourcePath);
        var diskB = GetDiskForPath(destPath);
        bool isSame = diskA != null && diskB != null && diskA.DeviceId == diskB.DeviceId;

        var profile = new TransferScenarioProfile
        {
            SourceDisk = diskA,
            DestinationDisk = diskB,
            IsSamePhysicalDisk = isSame
        };

        // Логика подбора буферов и параллелизма
        if (isSame)
        {
            if (diskA?.MediaType == StorageMediaType.HDD)
            {
                // На одном HDD конкурентный доступ убивает скорость (seek storm).
                // Решение: 1 поток, большой кольцевой буфер 4 МБ для длинных последовательных блоков.
                profile.RecommendedBufferSize = 4 * 1024 * 1024;
                profile.RecommendedConcurrency = 1;
                profile.StrategyName = "HDD Same-Disk (Zero Seek Contention)";
                profile.Description = "Источник и приёмник на одном шпинделе: строго 1 поток для устранения перемещений головок.";
            }
            else
            {
                // На одном NVMe/SSD
                profile.RecommendedBufferSize = 2 * 1024 * 1024;
                profile.RecommendedConcurrency = 4;
                profile.StrategyName = "SSD Same-Drive High-Throughput";
                profile.Description = "Параллельный доступ на одном SSD с буфером 2 МБ.";
            }
        }
        else
        {
            // Разные физические накопители
            if (diskA?.MediaType == StorageMediaType.NVMe && diskB?.MediaType == StorageMediaType.NVMe)
            {
                // NVMe to NVMe (PCIe to PCIe): Полная утилизация шины
                profile.RecommendedBufferSize = 4 * 1024 * 1024;
                profile.RecommendedConcurrency = Math.Clamp(LogicalCoreCount, 4, 16);
                profile.StrategyName = "NVMe-to-NVMe PCIe Full Throttle";
                profile.Description = "Максимальный параллелизм с глубокой очередью (QD 8-16) и 4 МБ буферами.";
            }
            else if (diskA?.MediaType == StorageMediaType.HDD || diskB?.MediaType == StorageMediaType.HDD)
            {
                // Один из участников — HDD: скорость ограничена механикой (120-220 МБ/с).
                profile.RecommendedBufferSize = 2 * 1024 * 1024;
                profile.RecommendedConcurrency = 2;
                profile.StrategyName = "Sequential HDD Adaptive Pipeline";
                profile.Description = "Ограничение параллелизма до 2 потоков для сохранения последовательной скорости HDD.";
            }
            else if (diskA?.MediaType == StorageMediaType.USB || diskB?.MediaType == StorageMediaType.USB)
            {
                profile.RecommendedBufferSize = 512 * 1024;
                profile.RecommendedConcurrency = 2;
                profile.StrategyName = "USB Bulk Streaming";
                profile.Description = "Оптимизированные 512 КБ буферы для USB-хоста.";
            }
            else
            {
                // SATA SSD -> SATA SSD
                profile.RecommendedBufferSize = 1024 * 1024;
                profile.RecommendedConcurrency = Math.Clamp(LogicalCoreCount / 2, 2, 8);
                profile.StrategyName = "SATA SSD High-Speed Stream";
                profile.Description = "Стриминг 1 МБ блоками для насыщения интерфейса SATA 6 Гбит/с (~550 МБ/с).";
            }
        }

        return profile;
    }
}
