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

    /// <summary>
    /// Свободное место НЕ является показателем здоровья носителя.
    /// Почти пустой диск не «здоровее» почти заполненного — он просто пустее.
    /// Раньше оно входило в взвешенную сумму с весом 0.25, наравне с реальным
    /// здоровьем (0.30) и температурой (0.20). Из-за этого диск, о котором
    /// приложение не знает ничего (нет S.M.A.R.T., нет датчика температуры),
    /// получал 100/100 и «A+» — исключительно за наличие свободного места.
    ///
    /// Теперь свободное место остаётся справочным полем (SpaceScore),
    /// но в оценку состояния не входит.
    /// </summary>
    public bool IsCalculated => HasHealth || HasTemperature || HasLatency || HasWear;

    /// <summary>Сколько именно показателей состояния удалось измерить (из 4 возможных).</summary>
    public int MeasuredComponentCount =>
        (HasHealth ? 1 : 0) + (HasTemperature ? 1 : 0) + (HasLatency ? 1 : 0) + (HasWear ? 1 : 0);

    /// <summary>
    /// Насколько оценка надёжна. Один измеренный параметр из четырёх — это
    /// очень слабое основание для вердикта «Отличное состояние», поэтому
    /// интерфейс должен это показывать, а не выдавать уверенный «A+».
    /// </summary>
    public double ConfidencePercent => MeasuredComponentCount * 100.0 / 4.0;

    /// <summary>Что именно измерено — чтобы пользователь понимал, на чём основан вердикт.</summary>
    public string BasisDescription
    {
        get
        {
            var parts = new List<string>(4);
            if (HasHealth) parts.Add("состояние контроллера");
            if (HasTemperature) parts.Add("температура");
            if (HasWear) parts.Add("износ");
            if (HasLatency) parts.Add("задержка I/O");
            return parts.Count == 0
                ? "Ни один показатель состояния не измерен"
                : "На основе: " + string.Join(", ", parts);
        }
    }

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
