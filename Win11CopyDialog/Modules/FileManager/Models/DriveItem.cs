using System.IO;
using Win11CopyDialog.Helpers;

namespace Win11CopyDialog.Modules.FileManager.Models;

public sealed class DriveItem
{
    public string Name { get; set; } = "";
    public string RootDirectory { get; set; } = "";
    public string VolumeLabel { get; set; } = "";
    public string DriveType { get; set; } = "";
    public long TotalSize { get; set; }
    public long FreeSpace { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(VolumeLabel)
        ? $"{Name} ({DriveType})" : $"{VolumeLabel} ({Name})";

    public string SpaceFormatted => TotalSize > 0
        ? $"{Formatters.Bytes(FreeSpace)} свободно из {Formatters.Bytes(TotalSize)}"
        : "Готов к работе";

    public double PercentUsed
    {
        get => TotalSize > 0 ? (double)(TotalSize - FreeSpace) / TotalSize * 100.0 : 0.0;
        set { }
    }

    public string Icon => DriveType switch
    {
        "Fixed" => "🖴",
        "Removable" => "💾",
        "Network" => "🌐",
        "CDRom" => "💿",
        _ => "🖴"
    };
}

public sealed class QuickAccessItem
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Icon { get; set; } = "📁";
}
