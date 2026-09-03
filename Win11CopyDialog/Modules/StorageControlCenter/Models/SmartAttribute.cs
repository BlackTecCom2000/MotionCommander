namespace Win11CopyDialog.Modules.StorageControlCenter.Models;

public sealed class SmartAttribute
{
    public byte Id { get; set; }
    public string Name { get; set; } = "";
    public int Current { get; set; }
    public int Worst { get; set; }
    public int Threshold { get; set; }
    public long RawValue { get; set; }
    public string RawValueFormatted { get; set; } = "";
    public string Status { get; set; } = "Good";
    public bool IsCritical { get; set; }
    public string Description { get; set; } = "";

    public string StatusGlyph => Status switch
    {
        "Good" => "✔",
        "Warning" => "⚠",
        "Critical" => "✖",
        _ => "•"
    };

    public string StatusColorToken => Status switch
    {
        "Good" => "SuccessGreenBrush",
        "Warning" => "WarningAmberBrush",
        "Critical" => "ErrorRedBrush",
        _ => "MutedTextBrush"
    };
}
