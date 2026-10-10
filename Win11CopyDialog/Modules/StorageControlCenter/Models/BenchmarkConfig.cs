namespace Win11CopyDialog.Modules.StorageControlCenter.Models;

public sealed class BenchmarkConfig
{
    public string TargetDrive { get; set; } = "C:\\";
    public int FileSizeBytes { get; set; } = 256 * 1024 * 1024; // 256 MB default
    public int Passes { get; set; } = 1;
    public int QueueDepth { get; set; } = 32;
    public int Threads { get; set; } = 1;
    public bool TestSequentialRead { get; set; } = true;
    public bool TestSequentialWrite { get; set; } = true;
    public bool TestRandom4KRead { get; set; } = true;
    public bool TestRandom4KWrite { get; set; } = true;
    public bool TestMixed { get; set; } = true;
}

public sealed class StorageBenchmarkItem
{
    public string TestType { get; set; } = "";
    public string BlockSize { get; set; } = "";
    public string QueueThreads { get; set; } = "Q1T1";
    public double ReadSpeedMBps { get; set; }
    public double WriteSpeedMBps { get; set; }
    public double ReadIops { get; set; }
    public double WriteIops { get; set; }
    public double ReadLatencyUs { get; set; }
    public double WriteLatencyUs { get; set; }
    public string Status { get; set; } = "Ready";

    public string DataSourceType { get; set; } = "Прямой Direct I/O (без кэша Windows)";
    public string TestConditionsSummary { get; set; } = "WriteThrough • Синхронный сброс буферов на флеш-память";
    public string LimitationsSummary { get; set; } = "Однопоточный режим Q1T1. Пиковая скорость NVMe при Q32/Q64 может быть выше.";
    public bool IsCached { get; set; } = false;

    public string ReadSpeedFormatted => ReadSpeedMBps >= 1000.0 ? $"{ReadSpeedMBps / 1024.0:F2} ГБ/с" : $"{ReadSpeedMBps:F1} МБ/с";
    public string WriteSpeedFormatted => WriteSpeedMBps >= 1000.0 ? $"{WriteSpeedMBps / 1024.0:F2} ГБ/с" : $"{WriteSpeedMBps:F1} МБ/с";
    public string ReadIopsFormatted => $"{ReadIops:N0} IOPS";
    public string WriteIopsFormatted => $"{WriteIops:N0} IOPS";
}

public sealed class StorageBenchmarkSessionResult
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string DiskModel { get; set; } = "";
    public string DriveLetter { get; set; } = "";
    public List<StorageBenchmarkItem> Items { get; set; } = new();
    public double OverallPerformanceScore { get; set; }
    public double MaxTemperatureObserved { get; set; }
    public double AverageCpuUsagePercent { get; set; }
    public bool TempFilesCleanedUp { get; set; } = true;
    public string TempCleanupStatus { get; set; } = "✓ Временный тестовый файл bench.dat удален (0 байт оставлено на накопителе)";
    public string LimitationsDisclaimer { get; set; } = "Измерения выполнены с обходом кэша ОС Windows. Результат отражает физическую скорость накопителя в одном потоке.";
}
