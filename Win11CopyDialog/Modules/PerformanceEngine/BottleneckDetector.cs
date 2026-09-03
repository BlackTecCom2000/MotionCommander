namespace Win11CopyDialog.Modules.PerformanceEngine;

public enum BottleneckType
{
    BalancedMaxThroughput,
    SourceLimited,
    DestinationLimited,
    CpuLimited,
    MemoryLimited,
    IopsLimited,
    SystemContention
}

public sealed class BottleneckAnalysisResult
{
    public BottleneckType Type { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Recommendation { get; set; } = "";
    public string BadgeColorHex { get; set; } = "#10B981"; // Emerald green
    public string BadgeIcon { get; set; } = "⚡";
}

/// <summary>
/// Анализатор узких мест (Bottleneck Detector) в реальном времени:
/// анализирует соотношение задержек чтения/записи, загрузку процессора,
/// давление на память и характеристики накопителей, определяя лимитирующий фактор.
/// </summary>
public static class BottleneckDetector
{
    public static BottleneckAnalysisResult Analyze(
        double currentThroughputBytesPerSec,
        double readLatencyMs,
        double writeLatencyMs,
        double cpuUsagePercent,
        double memoryAvailableGb,
        StorageMediaType srcMedia,
        StorageMediaType dstMedia,
        bool isSmallFilesBatch)
    {
        var result = new BottleneckAnalysisResult();

        // 1. Проверка на IOPS / Small Files
        if (isSmallFilesBatch && currentThroughputBytesPerSec < 40 * 1024 * 1024)
        {
            result.Type = BottleneckType.IopsLimited;
            result.Title = "Лимит IOPS (Множество мелких файлов)";
            result.Description = "Производительность упирается в транзакции метаданных файловой системы (MFT/FAT).";
            result.Recommendation = "Задействован параллельный пакетный конвейер мелких файлов.";
            result.BadgeColorHex = "#F59E0B"; // Amber
            result.BadgeIcon = "📁";
            return result;
        }

        // 2. Проверка загрузки процессора (актуально при шифровании или сжатии)
        if (cpuUsagePercent > 88.0)
        {
            result.Type = BottleneckType.CpuLimited;
            result.Title = "Лимит CPU";
            result.Description = $"Процессор загружен на {cpuUsagePercent:F0}%. Алгоритмы обработки или сторонние процессы насыщают ядра.";
            result.Recommendation = "Уменьшен приоритет вычислений для разгрузки I/O.";
            result.BadgeColorHex = "#EF4444"; // Red
            result.BadgeIcon = "🧠";
            return result;
        }

        // 3. Проверка давления на память
        if (memoryAvailableGb < 1.0)
        {
            result.Type = BottleneckType.MemoryLimited;
            result.Title = "Давление на RAM";
            result.Description = $"Свободно всего {memoryAvailableGb:F1} ГБ оперативной памяти. Windows может выполнять сброс страниц на диск.";
            result.Recommendation = "Используются компактные пулы буферов.";
            result.BadgeColorHex = "#EF4444";
            result.BadgeIcon = "💾";
            return result;
        }

        // 4. Сравнение задержек чтения и записи
        if (writeLatencyMs > readLatencyMs * 2.5 && writeLatencyMs > 25.0)
        {
            result.Type = BottleneckType.DestinationLimited;
            result.Title = "Лимит накопителя-приёмника";
            string reason = dstMedia == StorageMediaType.HDD ? "Механический HDD выполняет запись медленнее чтения." :
                            dstMedia == StorageMediaType.USB ? "Интерфейс USB ограничивает поток записи." :
                            "Запись на целевой накопитель лимитирует скорость (возможно, заполнен SLC-кэш).";
            result.Description = $"{reason} Задержка записи: {writeLatencyMs:F1} мс vs чтение {readLatencyMs:F1} мс.";
            result.Recommendation = "Чтение временно притормаживается, чтобы не переполнять очередь приёмника.";
            result.BadgeColorHex = "#3B82F6"; // Blue
            result.BadgeIcon = "📥";
            return result;
        }

        if (readLatencyMs > writeLatencyMs * 2.5 && readLatencyMs > 25.0)
        {
            result.Type = BottleneckType.SourceLimited;
            result.Title = "Лимит накопителя-источника";
            string reason = srcMedia == StorageMediaType.HDD ? "Медленное чтение с механического HDD." :
                            srcMedia == StorageMediaType.USB ? "Низкая скорость считывания с USB." :
                            "Чтение с исходного накопителя медленнее возможностей приёмника.";
            result.Description = $"{reason} Задержка чтения: {readLatencyMs:F1} мс vs запись {writeLatencyMs:F1} мс.";
            result.Recommendation = "Приёмник ожидает поступления порций данных.";
            result.BadgeColorHex = "#EC4899"; // Pink
            result.BadgeIcon = "📤";
            return result;
        }

        // 5. Оптимальное состояние: шина насыщена
        result.Type = BottleneckType.BalancedMaxThroughput;
        result.Title = "Максимальный сбалансированный поток";
        result.Description = "Чтение и запись работают синхронно на максимальной скорости накопителей.";
        result.Recommendation = "Оптимальная утилизация оборудования.";
        result.BadgeColorHex = "#10B981"; // Emerald Green
        result.BadgeIcon = "⚡";
        return result;
    }
}
