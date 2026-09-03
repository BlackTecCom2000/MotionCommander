namespace MotionCommander.Core.Archive;

public enum ArchiveFormat
{
    Zip,
    SevenZip,
    Tar,
    TarGz,
    TarBz2,
    TarXz
}

public sealed class ArchiveItem
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public long CompressedSize { get; set; }
    public DateTime LastModified { get; set; }
}

public sealed class ArchiveProgress
{
    public string CurrentEntryName { get; set; } = "";
    public long CurrentBytes { get; set; }
    public long TotalBytes { get; set; }
    public int Percent { get; set; }
    public double SpeedMBps { get; set; }
}
