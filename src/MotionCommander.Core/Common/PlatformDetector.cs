using System.Runtime.InteropServices;

namespace MotionCommander.Core.Common;

public enum TargetPlatform
{
    Windows,
    Linux,
    MacOS,
    Unknown
}

public static class PlatformDetector
{
    public static TargetPlatform CurrentPlatform
    {
        get
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return TargetPlatform.Windows;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return TargetPlatform.Linux;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return TargetPlatform.MacOS;
            return TargetPlatform.Unknown;
        }
    }

    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    public static bool IsMacOS => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    public static string OsName => CurrentPlatform switch
    {
        TargetPlatform.Windows => "Windows",
        TargetPlatform.Linux => "Linux",
        TargetPlatform.MacOS => "macOS",
        _ => "Unix-like"
    };

    public static string Architecture => RuntimeInformation.OSArchitecture.ToString();
    public static string OsDescription => RuntimeInformation.OSDescription;
    public static int LogicalCoreCount => Environment.ProcessorCount;
}
