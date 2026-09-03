namespace Win11CopyDialog.Modules.StorageControlCenter.Models;

public enum StorageRiskLevel
{
    SAFE,
    CAUTION,
    DESTRUCTIVE,
    IRREVERSIBLE
}

public sealed class StorageOperationLog
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string OperationName { get; set; } = "";
    public string TargetDescription { get; set; } = "";
    public StorageRiskLevel RiskLevel { get; set; } = StorageRiskLevel.SAFE;
    public string Status { get; set; } = "Успешно";
    public string Details { get; set; } = "";

    public string TimeFormatted => Timestamp.ToString("HH:mm:ss");

    public string RiskBadgeColor => RiskLevel switch
    {
        StorageRiskLevel.IRREVERSIBLE => "#EF4444",
        StorageRiskLevel.DESTRUCTIVE => "#F97316",
        StorageRiskLevel.CAUTION => "#F59E0B",
        _ => "#10B981"
    };
}
