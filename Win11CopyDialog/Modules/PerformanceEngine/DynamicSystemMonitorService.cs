using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using Win11CopyDialog.Helpers;

namespace Win11CopyDialog.Modules.PerformanceEngine;

public sealed class MonitorSample
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public double CpuPercent { get; set; }
    public double RamPercent { get; set; }
    public double RamUsedGb { get; set; }
    public double RamTotalGb { get; set; }
    public double DiskReadMBps { get; set; }
    public double DiskWriteMBps { get; set; }
    public double DiskTotalMBps => DiskReadMBps + DiskWriteMBps;
    public double DiskActivePercent { get; set; }
    public double DiskQueueDepth { get; set; }
    public double DiskIops { get; set; }
    public double DiskLatencyMs { get; set; }
    public double DiskTempC { get; set; }
    public bool HasDiskTemp { get; set; }
}

public sealed class BaselineComparisonDelta
{
    public MonitorSample Baseline { get; set; } = new();
    public MonitorSample Current { get; set; } = new();

    public double CpuDeltaPercent => Current.CpuPercent - Baseline.CpuPercent;
    public double RamDeltaGb => Current.RamUsedGb - Baseline.RamUsedGb;
    public double DiskSpeedDeltaMBps => Current.DiskTotalMBps - Baseline.DiskTotalMBps;
    public double DiskTempDeltaC => (Current.HasDiskTemp && Baseline.HasDiskTemp)
        ? Current.DiskTempC - Baseline.DiskTempC
        : 0;

    public string CpuDeltaFormatted =>
        CpuDeltaPercent <= 0
            ? $"{CpuDeltaPercent:F1}% (улучшение)"
            : $"+{CpuDeltaPercent:F1}% (рост нагрузки)";

    public string RamDeltaFormatted =>
        RamDeltaGb <= 0
            ? $"{RamDeltaGb:F2} ГБ (освобождено)"
            : $"+{RamDeltaGb:F2} ГБ (дополнительно занято)";

    public string DiskSpeedDeltaFormatted =>
        DiskSpeedDeltaMBps >= 0
            ? $"+{DiskSpeedDeltaMBps:F1} МБ/с (прирост)"
            : $"{DiskSpeedDeltaMBps:F1} МБ/с (снижение)";

    public string DiskTempDeltaFormatted =>
        (Current.HasDiskTemp && Baseline.HasDiskTemp)
            ? (DiskTempDeltaC <= 0 ? $"{DiskTempDeltaC:F1} °C (охлаждение)" : $"+{DiskTempDeltaC:F1} °C (нагрев)")
            : "н/д";
}

public static class DynamicSystemMonitorService
{
    private static readonly object _lock = new();
    private static readonly List<MonitorSample> _history = new(120);
    private const int MaxHistorySamples = 60; // 60 секунд истории

    public static MonitorSample? BaselineSnapshot { get; private set; }
    public static bool HasBaseline => BaselineSnapshot != null;

    public static MonitorSample CaptureCurrentSample()
    {
        lock (_lock)
        {
            var sys = SystemResourceMonitor.GetSnapshot();
            var sample = new MonitorSample
            {
                Timestamp = DateTime.Now,
                CpuPercent = Math.Round(sys.CpuTotalPercent, 1),
                RamPercent = Math.Round(sys.MemoryUsagePercent, 1),
                RamUsedGb = Math.Round(sys.TotalMemoryGb - sys.AvailableMemoryGb, 2),
                RamTotalGb = Math.Round(sys.TotalMemoryGb, 1)
            };

            // Чтение суммарной дисковой активности через Win32_PerfFormattedData_PerfDisk_PhysicalDisk
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, DiskReadBytesPersec, DiskWriteBytesPersec, PercentDiskTime, CurrentDiskQueueLength, DiskTransfersPersec, AvgDiskSecPerTransfer " +
                    "FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk WHERE Name='_Total'");
                using var coll = searcher.Get();
                foreach (ManagementObject mo in coll)
                {
                    double readBps = Convert.ToDouble(mo["DiskReadBytesPersec"] ?? 0);
                    double writeBps = Convert.ToDouble(mo["DiskWriteBytesPersec"] ?? 0);
                    double activePct = Convert.ToDouble(mo["PercentDiskTime"] ?? 0);
                    double queue = Convert.ToDouble(mo["CurrentDiskQueueLength"] ?? 0);
                    double iops = Convert.ToDouble(mo["DiskTransfersPersec"] ?? 0);
                    double secPerTransfer = Convert.ToDouble(mo["AvgDiskSecPerTransfer"] ?? 0);

                    sample.DiskReadMBps = Math.Round(readBps / (1024.0 * 1024.0), 1);
                    sample.DiskWriteMBps = Math.Round(writeBps / (1024.0 * 1024.0), 1);
                    sample.DiskActivePercent = Math.Min(100.0, Math.Round(activePct, 1));
                    sample.DiskQueueDepth = Math.Round(queue, 1);
                    sample.DiskIops = Math.Round(iops, 0);
                    sample.DiskLatencyMs = Math.Round(secPerTransfer * 1000.0, 1);
                    break;
                }
            }
            catch { }

            // Опрос температуры накопителя через WMI (если доступна)
            try
            {
                var disks = StorageControlCenter.Services.StorageDiscoveryService.GetAllDisks();
                var hotDisk = disks.FirstOrDefault(d => d.HasTemperature);
                if (hotDisk != null)
                {
                    sample.DiskTempC = hotDisk.TemperatureC;
                    sample.HasDiskTemp = true;
                }
            }
            catch { }

            _history.Add(sample);
            while (_history.Count > MaxHistorySamples)
            {
                _history.RemoveAt(0);
            }

            return sample;
        }
    }

    public static List<MonitorSample> GetHistory()
    {
        lock (_lock)
        {
            return _history.ToList();
        }
    }

    public static void SaveBaselineSnapshot()
    {
        lock (_lock)
        {
            BaselineSnapshot = _history.LastOrDefault() ?? CaptureCurrentSample();
        }
    }

    public static void ClearBaseline()
    {
        lock (_lock)
        {
            BaselineSnapshot = null;
        }
    }

    public static BaselineComparisonDelta? GetComparisonDelta()
    {
        lock (_lock)
        {
            if (BaselineSnapshot == null) return null;
            var current = _history.LastOrDefault() ?? CaptureCurrentSample();
            return new BaselineComparisonDelta
            {
                Baseline = BaselineSnapshot,
                Current = current
            };
        }
    }
}
