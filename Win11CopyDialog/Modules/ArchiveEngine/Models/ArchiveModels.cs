namespace Win11CopyDialog.Modules.ArchiveEngine.Models;

public enum ArchiveFormat
{
    Zip,
    SevenZip,
    Tar,
    TarGz,
    TarBz2,
    TarXz
}

public enum CompressionLevelPreset
{
    Store,      // Без сжатия (максимальная скорость)
    Fast,       // Быстрое сжатие
    Balanced,   // Оптимальный баланс
    Maximum,    // Максимальное сжатие
    Ultra       // Ультра сжатие
}

public sealed class ArchiveProgress
{
    public string CurrentFile { get; set; } = string.Empty;
    public long BytesProcessed { get; set; }
    public long TotalBytes { get; set; }
    public long CompressedBytes { get; set; }
    public double ProgressPercent => TotalBytes > 0 ? (double)BytesProcessed / TotalBytes * 100.0 : 0.0;
    public double RatioPercent => BytesProcessed > 0 ? (double)CompressedBytes / BytesProcessed * 100.0 : 100.0;
    public double SavedPercent => Math.Max(0.0, 100.0 - RatioPercent);
    public double CurrentSpeedBytesPerSec { get; set; }
}
