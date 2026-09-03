using System.Management;
using Win11CopyDialog.Modules.StorageControlCenter.Models;

namespace Win11CopyDialog.Modules.StorageControlCenter.Services;

public static class StorageMonitorService
{
    private static readonly object _lock = new();

    public static void PollRealtimeTelemetry(IEnumerable<StorageDisk> disks)
    {
        lock (_lock)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name, DiskReadBytesPersec, DiskWriteBytesPersec, CurrentDiskQueueLength, PercentDiskTime, AvgDiskSecPerTransfer, DiskTransfersPersec FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk");
                using var coll = searcher.Get();

                var diskMap = disks.ToDictionary(d => d.DiskNumber, d => d);

                foreach (ManagementObject obj in coll)
                {
                    string name = obj["Name"]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(name) || name == "_Total") continue;

                    // Name format: "0 C: D:" -> first token is disk number
                    int spaceIdx = name.IndexOf(' ');
                    string numStr = spaceIdx > 0 ? name.Substring(0, spaceIdx) : name;

                    if (int.TryParse(numStr, out int diskNum) && diskMap.TryGetValue(diskNum, out var disk))
                    {
                        double readBps = Convert.ToDouble(obj["DiskReadBytesPersec"] ?? 0);
                        double writeBps = Convert.ToDouble(obj["DiskWriteBytesPersec"] ?? 0);
                        double queue = Convert.ToDouble(obj["CurrentDiskQueueLength"] ?? 0);
                        double activePct = Convert.ToDouble(obj["PercentDiskTime"] ?? 0);
                        double secPerTransfer = Convert.ToDouble(obj["AvgDiskSecPerTransfer"] ?? 0);
                        double iops = Convert.ToDouble(obj["DiskTransfersPersec"] ?? 0);

                        disk.CurrentReadSpeedMBps = Math.Round(readBps / (1024.0 * 1024.0), 1);
                        disk.CurrentWriteSpeedMBps = Math.Round(writeBps / (1024.0 * 1024.0), 1);
                        disk.CurrentQueueDepth = Math.Round(queue, 1);
                        disk.ActiveTimePercent = Math.Min(100.0, Math.Round(activePct, 1));
                        disk.CurrentLatencyMs = Math.Round(secPerTransfer * 1000.0, 2);
                        disk.CurrentIops = Math.Round(iops, 0);
                    }
                }
            }
            catch { }
        }
    }
}
