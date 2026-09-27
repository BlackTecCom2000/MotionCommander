namespace Win11CopyDialog.Modules.StorageControlCenter.Models;

/// <summary>
/// Итоговая оценка состояния накопителя.
///
/// <para>Раньше все под-оценки по умолчанию равнялись 100.0, поэтому только что
/// созданный объект немедленно давал Grade «A+» и StatusText «Отличное
/// состояние» — ДО какого-либо измерения. Теперь неизмеренные компоненты
/// равны 0 и исключаются из расчёта, а при полном отсутствии данных оценка
/// не выставляется вовсе.</para>
/// </summary>
public sealed class StorageScore
{
    public double TotalScore { get; set; }
    public double HealthScore { get; set; }
    public double TemperatureScore { get; set; }
    public double SpaceScore { get; set; }
    public double LatencyScore { get; set; }
    public double WearScore { get; set; }

    /// <summary>
    /// Какие компоненты реально рассчитаны. Не рассчитанные не участвуют
    /// в взвешенной сумме.
    /// </summary>
    public bool HasHealth { get; set; }
    public bool HasTemperature { get; set; }
    public bool HasSpace { get; set; }
    public bool HasLatency { get; set; }
    public bool HasWear { get; set; }

    /// <summary>Истина, если оценка рассчитана хотя бы по одному реальному параметру.</summary>
    public bool IsCalculated => HasHealth || HasTemperature || HasSpace || HasLatency || HasWear;

    /// <summary>Буквенная оценка, либо честная «н/д», если данных нет.</summary>
    public string Grade => !IsCalculated ? "н/д" : TotalScore switch
    {
        >= 95 => "A+",
        >= 88 => "A",
        >= 75 => "B",
        >= 60 => "C",
        _ => "D"
    };

    public string StatusText => !IsCalculated ? "Нет данных для оценки" : TotalScore switch
    {
        >= 90 => "Отличное состояние",
        >= 75 => "Стабильное состояние",
        >= 60 => "Требует внимания",
        _ => "Критическое состояние"
    };

    public string StatusColor => !IsCalculated ? "#6B7280" : TotalScore switch
    {
        >= 85 => "#10B981", // Emerald Green
        >= 70 => "#3B82F6", // Accent Blue
        >= 50 => "#F59E0B", // Amber Warning
        _ => "#EF4444"      // Crimson Red
    };

    public List<string> Warnings { get; set; } = new();
    public List<string> Optimizations { get; set; } = new();
}
