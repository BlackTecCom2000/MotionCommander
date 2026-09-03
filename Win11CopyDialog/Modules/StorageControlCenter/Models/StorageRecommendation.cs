namespace Win11CopyDialog.Modules.StorageControlCenter.Models;

public enum RecommendationSeverity
{
    Info,
    Warning,
    Critical
}

public enum RecommendationCategory
{
    Trim,
    Defrag,
    Cleanup,
    Space,
    Thermal,
    Health,
    Performance,
    Security
}

public sealed class StorageRecommendation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public RecommendationCategory Category { get; set; } = RecommendationCategory.Performance;
    public RecommendationSeverity Severity { get; set; } = RecommendationSeverity.Info;
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string ActionText { get; set; } = "";
    public string ActionCommand { get; set; } = "";
    public string EstimatedBenefit { get; set; } = "";
    public int TargetDiskNumber { get; set; } = -1;
    public string TargetDriveLetter { get; set; } = "";

    public string SeverityIcon => Severity switch
    {
        RecommendationSeverity.Critical => "🚨",
        RecommendationSeverity.Warning => "⚠️",
        _ => "💡"
    };

    public string SeverityBadgeColor => Severity switch
    {
        RecommendationSeverity.Critical => "#EF4444",
        RecommendationSeverity.Warning => "#F59E0B",
        _ => "#3B82F6"
    };
}
