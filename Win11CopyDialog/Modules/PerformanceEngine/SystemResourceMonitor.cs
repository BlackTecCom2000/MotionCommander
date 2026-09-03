using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Win11CopyDialog.Modules.PerformanceEngine;

public sealed class SystemResourceSnapshot
{
    public double CpuTotalPercent { get; set; }
    public double ProcessCpuPercent { get; set; }
    public double AvailableMemoryGb { get; set; }
    public double TotalMemoryGb { get; set; }
    public double MemoryUsagePercent { get; set; }
}

/// <summary>
/// Легковесный монитор системных ресурсов на базе низкоуровневых функций Windows API:
/// измеряет общую загрузку процессора, потребление памяти и активность без накладных расходов.
/// </summary>
public static class SystemResourceMonitor
{
    private static long _prevIdleTime;
    private static long _prevKernelTime;
    private static long _prevUserTime;
    private static DateTime _lastCpuSampleTime = DateTime.MinValue;
    private static double _lastCpuUsage = 0;
    private static readonly object _cpuLock = new();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long lpIdleTime, out long lpKernelTime, out long lpUserTime);

    public static SystemResourceSnapshot GetSnapshot()
    {
        double cpu = GetCpuUsage();
        var (totalBytes, freeBytes) = HardwareAnalyzer.GetSystemMemoryInfo();

        double totalGb = totalBytes / (1024.0 * 1024.0 * 1024.0);
        double freeGb = freeBytes / (1024.0 * 1024.0 * 1024.0);
        double usedPercent = totalGb > 0 ? (totalGb - freeGb) / totalGb * 100.0 : 0;

        return new SystemResourceSnapshot
        {
            CpuTotalPercent = cpu,
            TotalMemoryGb = totalGb,
            AvailableMemoryGb = freeGb,
            MemoryUsagePercent = usedPercent
        };
    }

    private static double GetCpuUsage()
    {
        lock (_cpuLock)
        {
            var now = DateTime.Now;
            if ((now - _lastCpuSampleTime).TotalMilliseconds < 250)
            {
                return _lastCpuUsage;
            }

            if (!GetSystemTimes(out long idleTime, out long kernelTime, out long userTime))
            {
                return _lastCpuUsage;
            }

            if (_prevKernelTime == 0)
            {
                _prevIdleTime = idleTime;
                _prevKernelTime = kernelTime;
                _prevUserTime = userTime;
                _lastCpuSampleTime = now;
                return 5.0; // Значение по умолчанию для первого отсчета
            }

            long usrDiff = userTime - _prevUserTime;
            long kerDiff = kernelTime - _prevKernelTime;
            long idlDiff = idleTime - _prevIdleTime;

            long sysTotal = usrDiff + kerDiff;
            long total = sysTotal;

            if (total > 0)
            {
                _lastCpuUsage = Math.Clamp((1.0 - (double)idlDiff / total) * 100.0, 0.0, 100.0);
            }

            _prevIdleTime = idleTime;
            _prevKernelTime = kernelTime;
            _prevUserTime = userTime;
            _lastCpuSampleTime = now;

            return _lastCpuUsage;
        }
    }
}
