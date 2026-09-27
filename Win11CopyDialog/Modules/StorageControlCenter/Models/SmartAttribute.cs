namespace Win11CopyDialog.Modules.StorageControlCenter.Models;

/// <summary>
/// Одна реально прочитанная запись S.M.A.R.T.
///
/// <para>Раньше все строки создавались как литералы со значениями
/// Current=100 / Worst=100 / RawValue=0 / Status="Good", включая
/// критические атрибуты 0x05 (Reallocated), 0xC5 (Pending) и 0xC6
/// (Uncorrectable). Из-за этого диск с реальными ошибками всегда
/// отображался как полностью исправный, и признать поломку было
/// невозможно.</para>
///
/// <para>Теперь Status по умолчанию — «Неизвестно», и он меняется только
/// после сравнения измеренного значения с реальным порогом.</para>
/// </summary>
public sealed class SmartAttribute
{
    public byte Id { get; set; }
    public string Name { get; set; } = "";

    public int Current { get; set; }
    public int Worst { get; set; }

    /// <summary>
    /// Порог из SMART-таблицы. Может быть недоступен у некоторых контроллеров;
    /// при <c>HasThreshold == false</c> статус не вычисляется.
    /// </summary>
    public int Threshold { get; set; }
    public bool HasThreshold { get; set; }

    public long RawValue { get; set; }
    public string RawValueFormatted { get; set; } = "";

    /// <summary>Good / Warning / Critical / Неизвестно</summary>
    public string Status { get; set; } = "Неизвестно";
    public bool IsCritical { get; set; }
    public string Description { get; set; } = "";

    public string StatusGlyph => Status switch
    {
        "Good" => "✔",
        "Warning" => "⚠",
        "Critical" => "✖",
        _ => "❔"
    };

    public string StatusColorToken => Status switch
    {
        "Good" => "SuccessGreenBrush",
        "Warning" => "WarningAmberBrush",
        "Critical" => "ErrorRedBrush",
        _ => "MutedTextBrush"
    };

    /// <summary>
    /// Вычисляет статус по правилам S.M.A.R.T: значение ниже порога — это
    /// отказ. Если порог неизвестен, статус остаётся «Неизвестно».
    /// </summary>
    public static string StatusFromThreshold(int current, int threshold, bool hasThreshold)
    {
        if (!hasThreshold) return "Неизвестно";
        if (current == 0) return "Неизвестно";   // Many controllers do not report normalized values
        if (current <= threshold) return "Critical";
        return "Good";
    }
}
