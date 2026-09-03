namespace Win11CopyDialog.Modules.StorageControlCenter.Models;

public sealed class StorageScore
{
    public double TotalScore { get; set; } = 100.0;
    public double HealthScore { get; set; } = 100.0;
    public double TemperatureScore { get; set; } = 100.0;
    public double SpaceScore { get; set; } = 100.0;
    public double LatencyScore { get; set; } = 100.0;
    public double WearScore { get; set; } = 100.0;

    public string Grade => TotalScore switch
    {
        >= 95 => "A+",
        >= 88 => "A",
        >= 75 => "B",
        >= 60 => "C",
        _ => "D"
    };

    public string StatusText => TotalScore switch
    {
        >= 90 => "Отличное состояние",
        >= 75 => "Стабильное состояние",
        >= 60 => "Требует внимания",
        _ => "Критическое состояние"
    };

    public string StatusColor => TotalScore switch
    {
        >= 85 => "#10B981", // Emerald Green
        >= 70 => "#3B82F6", // Accent Blue
        >= 50 => "#F59E0B", // Amber Warning
        _ => "#EF4444"      // Crimson Red
    };

    public List<string> Warnings { get; set; } = new();
    public List<string> Optimizations { get; set; } = new();
}
