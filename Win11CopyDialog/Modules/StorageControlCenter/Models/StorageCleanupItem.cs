using Win11CopyDialog.Helpers;

namespace Win11CopyDialog.Modules.StorageControlCenter.Models;

public sealed class StorageCleanupItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string PathLocation { get; set; } = "";
    public long SizeBytes { get; set; }
    public int FileCount { get; set; }
    public bool IsSelected { get; set; } = true;
    public bool IsProtected { get; set; }
    public string RiskLevel { get; set; } = "SAFE";

    public string SizeFormatted => Formatters.Bytes(SizeBytes);
}

public sealed class LargeFileItem
{
    public string FilePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Extension { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime LastModified { get; set; }

    public string SizeFormatted => Formatters.Bytes(SizeBytes);
    public string DateFormatted => LastModified.ToString("dd.MM.yyyy HH:mm");
}
