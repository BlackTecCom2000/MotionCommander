using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using Win11CopyDialog.Helpers;

namespace Win11CopyDialog.Modules.GameTuning;

public sealed class GpuInfo
{
    public string Name { get; init; } = "";
    public string DriverVersion { get; init; } = "";
    public double? AdapterRamGb { get; init; }
    public override string ToString()
        => string.IsNullOrWhiteSpace(DriverVersion) ? Name : $"{Name} (драйвер {DriverVersion})";
}

/// <summary>
/// Фактические параметры машины, на которых строятся рекомендации.
/// Всё, что не удалось прочитать, остаётся пустым и в интерфейсе честно
/// подписано «н/д» — подставлять типичные значения нельзя.
/// </summary>
public sealed class HardwareInfo
{
    public string CpuName { get; init; } = "";
    public int CpuCores { get; init; }
    public int CpuThreads { get; init; }
    public double? RamGb { get; init; }
    public List<GpuInfo> Gpus { get; init; } = new();
    public int DisplayWidth { get; init; }
    public int DisplayHeight { get; init; }
    public int RefreshHz { get; init; }
    public string OsCaption { get; init; } = "";
    public int WindowsBuild { get; init; }
    public bool IsLaptop { get; init; }
    public bool IsAdmin { get; init; }

    public string CpuLine => string.IsNullOrWhiteSpace(CpuName)
        ? "н/д"
        : $"{CpuName} — {CpuCores} ядер / {CpuThreads} потоков";

    public string RamLine => RamGb.HasValue ? $"{RamGb.Value:0.#} ГБ" : "н/д";

    public string GpuLine => Gpus.Count == 0 ? "н/д" : string.Join("; ", Gpus.Select(g => g.Name));

    public string DisplayLine => DisplayWidth > 0 && DisplayHeight > 0
        ? $"{DisplayWidth}×{DisplayHeight}" + (RefreshHz > 0 ? $" @ {RefreshHz} Гц" : "")
        : "н/д";

    public string OsLine => string.IsNullOrWhiteSpace(OsCaption)
        ? $"Windows, сборка {WindowsBuild}"
        : $"{OsCaption}, сборка {WindowsBuild}";

    public string FormFactorLine => IsLaptop ? "ноутбук" : "настольный ПК";

    public bool HasDiscreteGpu => Gpus.Count > 1;
}

public static class HardwareProbe
{
    public static HardwareInfo Probe()
    {
        string cpuName = "";
        int cores = 0;
        int threads = 0;
        double? ramGb = null;
        var gpus = new List<GpuInfo>();
        string osCaption = "";
        int build = Environment.OSVersion.Version.Build;
        bool laptop = false;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
            foreach (ManagementObject obj in searcher.Get().Cast<ManagementObject>())
            {
                cpuName = Convert.ToString(obj["Name"])?.Trim() ?? "";
                cores = ToInt(obj["NumberOfCores"]);
                threads = ToInt(obj["NumberOfLogicalProcessors"]);
                break;
            }
        }
        catch { }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Capacity FROM Win32_PhysicalMemory");
            long total = 0;
            foreach (ManagementObject obj in searcher.Get().Cast<ManagementObject>())
                total += ToLong(obj["Capacity"]);
            if (total > 0) ramGb = total / 1024d / 1024d / 1024d;
        }
        catch { }

        if (!ramGb.HasValue)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
                foreach (ManagementObject obj in searcher.Get().Cast<ManagementObject>())
                {
                    long total = ToLong(obj["TotalPhysicalMemory"]);
                    if (total > 0) ramGb = total / 1024d / 1024d / 1024d;
                    break;
                }
            }
            catch { }
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DriverVersion, AdapterRAM FROM Win32_VideoController");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ManagementObject obj in searcher.Get().Cast<ManagementObject>())
            {
                string name = Convert.ToString(obj["Name"])?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(name) || !names.Add(name)) continue;

                long ram = ToLong(obj["AdapterRAM"]);
                gpus.Add(new GpuInfo
                {
                    Name = name,
                    DriverVersion = Convert.ToString(obj["DriverVersion"])?.Trim() ?? "",
                    AdapterRamGb = ram > 0 ? ram / 1024d / 1024d / 1024d : null
                });
            }
        }
        catch { }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Caption FROM Win32_OperatingSystem");
            foreach (ManagementObject obj in searcher.Get().Cast<ManagementObject>())
            {
                osCaption = Convert.ToString(obj["Caption"])?.Trim() ?? "";
                break;
            }
        }
        catch { }

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Battery");
            laptop = searcher.Get().Count > 0;
        }
        catch { }

        (int width, int height, int hz) = ReadDisplayMode();

        return new HardwareInfo
        {
            CpuName = cpuName,
            CpuCores = cores,
            CpuThreads = threads,
            RamGb = ramGb,
            Gpus = gpus,
            DisplayWidth = width,
            DisplayHeight = height,
            RefreshHz = hz,
            OsCaption = osCaption,
            WindowsBuild = build,
            IsLaptop = laptop,
            IsAdmin = SuperAdminPrivilegeHelper.IsAdministrator()
        };
    }

    private const int EnumCurrentSettings = -1;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DevMode devMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DmDeviceName;
        public short DmSpecVersion;
        public short DmDriverVersion;
        public short DmSize;
        public short DmDriverExtra;
        public int DmFields;
        public int DmPositionX;
        public int DmPositionY;
        public int DmDisplayOrientation;
        public int DmDisplayFixedOutput;
        public short DmColor;
        public short DmDuplex;
        public short DmYResolution;
        public short DmTTOption;
        public short DmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DmFormName;
        public short DmLogPixels;
        public int DmBitsPerPel;
        public int DmPelsWidth;
        public int DmPelsHeight;
        public int DmDisplayFlags;
        public int DmDisplayFrequency;
        public int DmIcmMethod;
        public int DmIcmIntent;
        public int DmMediaType;
        public int DmDitherType;
        public int DmReserved1;
        public int DmReserved2;
        public int DmPanningWidth;
        public int DmPanningHeight;
    }

    private static (int width, int height, int hz) ReadDisplayMode()
    {
        try
        {
            var mode = new DevMode { DmDeviceName = "", DmFormName = "" };
            mode.DmSize = (short)Marshal.SizeOf<DevMode>();
            if (EnumDisplaySettings(null, EnumCurrentSettings, ref mode))
                return (mode.DmPelsWidth, mode.DmPelsHeight, mode.DmDisplayFrequency);
        }
        catch { }

        return (0, 0, 0);
    }

    private static int ToInt(object? value)
    {
        try { return value == null ? 0 : Convert.ToInt32(value); } catch { return 0; }
    }

    private static long ToLong(object? value)
    {
        try { return value == null ? 0 : Convert.ToInt64(value); } catch { return 0; }
    }
}
