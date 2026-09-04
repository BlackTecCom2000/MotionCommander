using System.IO;
using System.Windows;
using System.Windows.Media;
using Win11CopyDialog.Helpers;

namespace Win11CopyDialog.Modules.FileManager.Models;

public sealed class DriveItem
{
    public string Name { get; set; } = "";
    public string RootDirectory { get; set; } = "";
    public string VolumeLabel { get; set; } = "";
    public string DriveType { get; set; } = "Fixed";
    public string FileSystem { get; set; } = "NTFS";
    public long TotalSize { get; set; }
    public long FreeSpace { get; set; }

    public long UsedSpace => TotalSize > FreeSpace ? TotalSize - FreeSpace : 0;

    public bool IsSystemDrive => Name.StartsWith("C:", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(RootDirectory, Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase);

    public string CleanDriveLetter => Name.TrimEnd('\\', '/');

    public string Title => !string.IsNullOrWhiteSpace(VolumeLabel)
        ? $"{VolumeLabel} ({CleanDriveLetter})"
        : IsSystemDrive ? $"Системный диск ({CleanDriveLetter})" : $"Локальный диск ({CleanDriveLetter})";

    public string Subtitle => IsSystemDrive
        ? "Системный том Windows"
        : DriveType switch
        {
            "Removable" => "Съемный USB накопитель",
            "Network" => "Сетевой том данных",
            "CDRom" => "Оптический привод",
            _ => $"Локальный накопитель ({DriveBadge})"
        };

    public string DisplayName => Title;

    public double PercentUsed => TotalSize > 0 ? (double)(TotalSize - FreeSpace) / TotalSize * 100.0 : 0.0;
    public string PercentFormatted => $"{PercentUsed:F0}%";

    public string DriveBadge => !string.IsNullOrWhiteSpace(FileSystem) ? FileSystem.ToUpperInvariant() : "NTFS";

    public string FreeSpaceFormatted => TotalSize > 0 ? $"{Formatters.Bytes(FreeSpace)} свободно" : "Готов к работе";
    public string TotalSpaceFormatted => TotalSize > 0 ? $"из {Formatters.Bytes(TotalSize)}" : "";
    public string UsedSpaceFormatted => TotalSize > 0 ? $"{Formatters.Bytes(UsedSpace)} занято" : "";

    public string SpaceFormatted => TotalSize > 0
        ? $"{FreeSpaceFormatted} {TotalSpaceFormatted}"
        : "Готов к работе";

    public Brush UsageBrush => PercentUsed >= 90
        ? new SolidColorBrush(Color.FromRgb(239, 68, 68))
        : PercentUsed >= 75
            ? new SolidColorBrush(Color.FromRgb(245, 158, 11))
            : new SolidColorBrush(Color.FromRgb(6, 182, 212));

    public Brush UsageGradientBrush
    {
        get
        {
            if (PercentUsed >= 90)
            {
                var grad = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
                grad.GradientStops.Add(new GradientStop(Color.FromRgb(239, 68, 68), 0.0));
                grad.GradientStops.Add(new GradientStop(Color.FromRgb(185, 28, 28), 1.0));
                return grad;
            }
            if (PercentUsed >= 75)
            {
                var grad = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
                grad.GradientStops.Add(new GradientStop(Color.FromRgb(245, 158, 11), 0.0));
                grad.GradientStops.Add(new GradientStop(Color.FromRgb(217, 119, 6), 1.0));
                return grad;
            }
            else
            {
                var grad = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
                grad.GradientStops.Add(new GradientStop(Color.FromRgb(0, 240, 255), 0.0));
                grad.GradientStops.Add(new GradientStop(Color.FromRgb(37, 99, 235), 1.0));
                return grad;
            }
        }
    }

    public Brush BadgeBackgroundBrush => PercentUsed >= 90
        ? new SolidColorBrush(Color.FromArgb(40, 239, 68, 68))
        : PercentUsed >= 75
            ? new SolidColorBrush(Color.FromArgb(40, 245, 158, 11))
            : new SolidColorBrush(Color.FromArgb(32, 6, 182, 212));

    public Brush BadgeBorderBrush => PercentUsed >= 90
        ? new SolidColorBrush(Color.FromArgb(100, 239, 68, 68))
        : PercentUsed >= 75
            ? new SolidColorBrush(Color.FromArgb(100, 245, 158, 11))
            : new SolidColorBrush(Color.FromArgb(70, 6, 182, 212));

    public Brush IconBgBrush => IsSystemDrive
        ? new SolidColorBrush(Color.FromArgb(36, 0, 240, 255))
        : DriveType switch
        {
            "Removable" => new SolidColorBrush(Color.FromArgb(36, 16, 185, 129)),
            "Network" => new SolidColorBrush(Color.FromArgb(36, 139, 92, 246)),
            _ => new SolidColorBrush(Color.FromArgb(32, 59, 130, 246))
        };

    public Brush IconBorderBrush => IsSystemDrive
        ? new SolidColorBrush(Color.FromArgb(85, 0, 240, 255))
        : DriveType switch
        {
            "Removable" => new SolidColorBrush(Color.FromArgb(85, 16, 185, 129)),
            "Network" => new SolidColorBrush(Color.FromArgb(85, 139, 92, 246)),
            _ => new SolidColorBrush(Color.FromArgb(70, 59, 130, 246))
        };

    public Brush IconForegroundBrush => IsSystemDrive
        ? new SolidColorBrush(Color.FromRgb(0, 240, 255))
        : DriveType switch
        {
            "Removable" => new SolidColorBrush(Color.FromRgb(52, 211, 153)),
            "Network" => new SolidColorBrush(Color.FromRgb(167, 139, 250)),
            _ => new SolidColorBrush(Color.FromRgb(96, 165, 250))
        };

    public string VectorIconKey => IsSystemDrive
        ? "Icon_Drive_Windows"
        : DriveType switch
        {
            "Removable" => "Icon_Drive_USB",
            "Network" => "Icon_Drive_Network",
            _ => "Icon_Drive_SSD"
        };

    public Geometry? VectorGeometry => Application.Current?.TryFindResource(VectorIconKey) as Geometry;

    public string Glyph => IsSystemDrive ? "\uE782" : DriveType switch
    {
        "Removable" => "\uE88E",
        "Network" => "\uE753",
        "CDRom" => "\uE958",
        _ => "\uEDA2"
    };

    public string Icon => IsSystemDrive ? "🗔" : DriveType switch
    {
        "Removable" => "💾",
        "Network" => "🌐",
        "CDRom" => "💿",
        _ => "💽"
    };
}

public sealed class QuickAccessItem
{
    public string Name { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string Path { get; set; } = "";
    public string Icon { get; set; } = "📁";
    public string Glyph { get; set; } = "\uE770";
    public string VectorIconKey { get; set; } = "Icon_Folder";
    public string ColorHex { get; set; } = "#3B82F6";

    public Geometry? VectorGeometry => Application.Current?.TryFindResource(VectorIconKey) as Geometry;

    public Brush IconTintBrush
    {
        get
        {
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(ColorHex);
                return new SolidColorBrush(Color.FromArgb(36, c.R, c.G, c.B));
            }
            catch
            {
                return new SolidColorBrush(Color.FromArgb(36, 59, 130, 246));
            }
        }
    }

    public Brush IconBorderBrush
    {
        get
        {
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(ColorHex);
                return new SolidColorBrush(Color.FromArgb(85, c.R, c.G, c.B));
            }
            catch
            {
                return new SolidColorBrush(Color.FromArgb(75, 59, 130, 246));
            }
        }
    }

    public Brush IconForegroundBrush
    {
        get
        {
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(ColorHex);
                return new SolidColorBrush(c);
            }
            catch
            {
                return new SolidColorBrush(Color.FromRgb(59, 130, 246));
            }
        }
    }
}
