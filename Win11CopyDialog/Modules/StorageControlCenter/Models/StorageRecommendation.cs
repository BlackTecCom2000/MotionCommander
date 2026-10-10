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

public enum RecommendationRiskLevel
{
    Safe,       // 🟢 Безопасно для данных и системы
    Caution,    // 🟡 Требует внимания и понимания
    Risky       // 🔴 Потенциально рискованно (требует резервной копии)
}

public sealed class StorageRecommendation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public RecommendationCategory Category { get; set; } = RecommendationCategory.Performance;
    public RecommendationSeverity Severity { get; set; } = RecommendationSeverity.Info;
    public RecommendationRiskLevel Risk { get; set; } = RecommendationRiskLevel.Safe;

    public string Title { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>Конкретно: что проверить пользователю (оборудование, прокладки, кабели, настройки).</summary>
    public string WhatToCheck { get; set; } = "";

    /// <summary>Техническое обоснование: почему это поможет.</summary>
    public string WhyItHelps { get; set; } = "";

    /// <summary>Ожидаемый измеримый эффект от применения.</summary>
    public string ExpectedEffect { get; set; } = "";

    /// <summary>Пояснение безопасности и возможных рисков.</summary>
    public string RiskExplanation { get; set; } = "";

    /// <summary>На каких реальных данных основан вывод (WMI, SMART, телеметрия).</summary>
    public string DataBasis { get; set; } = "";

    /// <summary>Насколько уверенно сделан вывод (в процентах, например 95%).</summary>
    public int ConfidencePercent { get; set; } = 95;
    public string ConfidenceText => $"{ConfidencePercent}% ({GetConfidenceDescription(ConfidencePercent)})";

    /// <summary>Что именно изменится в системе/накопителе при нажатии кнопки действия.</summary>
    public string ExactSystemChanges { get; set; } = "";

    public string ActionText { get; set; } = "";
    public string ActionCommand { get; set; } = "";
    public string EstimatedBenefit { get; set; } = "";
    public int TargetDiskNumber { get; set; } = -1;
    public string TargetDriveLetter { get; set; } = "";

    private static string GetConfidenceDescription(int pct) => pct switch
    {
        >= 90 => "Высокая точность (Аппаратный датчик / WMI)",
        >= 70 => "Уверенная оценка (Эвристика системы)",
        _ => "Предварительная оценка"
    };

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

    public string RiskBadgeText => Risk switch
    {
        RecommendationRiskLevel.Safe => "🟢 Безопасно",
        RecommendationRiskLevel.Caution => "🟡 Требует внимания",
        RecommendationRiskLevel.Risky => "🔴 Рискованно (сделайте бэкап)",
        _ => "Безопасно"
    };

    public string RiskBadgeColor => Risk switch
    {
        RecommendationRiskLevel.Safe => "#10B981",
        RecommendationRiskLevel.Caution => "#F59E0B",
        RecommendationRiskLevel.Risky => "#EF4444",
        _ => "#10B981"
    };
}
