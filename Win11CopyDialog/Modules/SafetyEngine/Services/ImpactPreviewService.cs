using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Win11CopyDialog.Modules.SafetyEngine.Services;

public enum OperationImpactType
{
    FileDelete,
    StorageCleanup,
    PartitionFormat,
    PartitionModify,
    DiskWipe
}

public sealed class ImpactAssessment
{
    public string Title { get; set; } = "";
    public OperationImpactType OperationType { get; set; } = OperationImpactType.FileDelete;
    public string TargetSummary { get; set; } = "";
    public int AffectedItemsCount { get; set; }
    public long TotalBytes { get; set; }
    public string TotalSizeFormatted { get; set; } = "";
    public bool IsReversible { get; set; } = true;
    public string ReversibilityExplanation { get; set; } = "";
    public string RiskLevel { get; set; } = "Безопасно";
    public string RiskBadgeColorHex { get; set; } = "#10B981";
    public List<string> ImpactDetails { get; set; } = new();
    public bool HasSystemFilesWarning { get; set; }
    public string SystemFilesWarning { get; set; } = "";
    public string EmergencyPlan { get; set; } = "";
    public string AuditLogSummary { get; set; } = "";
}

public static class ImpactPreviewService
{
    private static readonly string AuditLogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "system_audit_log.txt");
    private static readonly List<string> _lastDeletedPaths = new();

    public static ImpactAssessment AssessFileDeletion(IEnumerable<string> paths, bool permanent)
    {
        var assessment = new ImpactAssessment
        {
            Title = permanent ? "Безвозвратное удаление файлов" : "Удаление файлов в Корзину",
            OperationType = OperationImpactType.FileDelete,
            IsReversible = !permanent
        };

        int count = 0;
        long bytes = 0;
        bool hasSys = false;
        var details = new List<string>();

        string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            count++;

            if (p.StartsWith(winDir, StringComparison.OrdinalIgnoreCase) ||
                p.StartsWith(progFiles, StringComparison.OrdinalIgnoreCase))
            {
                hasSys = true;
            }

            if (File.Exists(p))
            {
                try { bytes += new FileInfo(p).Length; } catch { }
                if (details.Count < 5) details.Add($"📄 {Path.GetFileName(p)} ({FormatSize(new FileInfo(p).Length)})");
            }
            else if (Directory.Exists(p))
            {
                try
                {
                    var di = new DirectoryInfo(p);
                    long dirBytes = 0;
                    int fCount = 0;
                    foreach (var f in di.EnumerateFiles("*", SearchOption.AllDirectories))
                    {
                        dirBytes += f.Length;
                        fCount++;
                        if (fCount > 5000) break; // ограничение для предпросмотра
                    }
                    bytes += dirBytes;
                    if (details.Count < 5) details.Add($"📁 {Path.GetFileName(p)} ({fCount} файлов, {FormatSize(dirBytes)})");
                }
                catch
                {
                    if (details.Count < 5) details.Add($"📁 {Path.GetFileName(p)}");
                }
            }
        }

        if (count > details.Count)
        {
            details.Add($"... и ещё {count - details.Count} элементов");
        }

        assessment.AffectedItemsCount = count;
        assessment.TotalBytes = bytes;
        assessment.TotalSizeFormatted = FormatSize(bytes);
        assessment.ImpactDetails = details;
        assessment.TargetSummary = $"Выбрано: {count} элементов ({assessment.TotalSizeFormatted})";

        if (hasSys)
        {
            assessment.HasSystemFilesWarning = true;
            assessment.SystemFilesWarning = "🚨 ВНИМАНИЕ: Выбранные элементы содержат файлы из системных каталогов Windows или Program Files! Их удаление может нарушить работу системы.";
            assessment.RiskLevel = "Критический риск";
            assessment.RiskBadgeColorHex = "#EF4444";
        }
        else if (permanent)
        {
            assessment.RiskLevel = "Внимание (Необратимо)";
            assessment.RiskBadgeColorHex = "#F59E0B";
        }
        else
        {
            assessment.RiskLevel = "Безопасно (В Корзину)";
            assessment.RiskBadgeColorHex = "#10B981";
        }

        if (permanent)
        {
            assessment.ReversibilityExplanation = "❌ НЕОБРАТИМОЕ ДЕЙСТВИЕ: Файлы будут стерты мимо Корзины. Восстановление стандартными средствами Windows невозможно.";
            assessment.EmergencyPlan = "Перед удалением критических данных убедитесь в наличии резервной копии на внешнем накопителе.";
        }
        else
        {
            assessment.ReversibilityExplanation = "✓ ОБРАТИМОЕ ДЕЙСТВИЕ: Элементы перемещаются в Корзину Windows. Их можно восстановить в любой момент.";
            assessment.EmergencyPlan = "Для отмены откройте Корзину Windows или нажмите «Отменить удаление» в журнале действий.";
        }

        assessment.AuditLogSummary = $"Удаление: {count} элементов ({assessment.TotalSizeFormatted}), режим: {(permanent ? "Permanent" : "RecycleBin")}";
        return assessment;
    }

    public static ImpactAssessment AssessStorageCleanup(string targetDrive, long estimatedBytesToFree, List<string> categoryNames)
    {
        var assessment = new ImpactAssessment
        {
            Title = "Очистка дискового пространства",
            OperationType = OperationImpactType.StorageCleanup,
            AffectedItemsCount = categoryNames.Count,
            TotalBytes = estimatedBytesToFree,
            TotalSizeFormatted = FormatSize(estimatedBytesToFree),
            IsReversible = false,
            RiskLevel = "Безопасно",
            RiskBadgeColorHex = "#10B981",
            TargetSummary = $"Накопитель {targetDrive} • Будет освобождено ~{FormatSize(estimatedBytesToFree)}",
            ReversibilityExplanation = "Удаляются временные кэши ОС, журналы дампов и временные файлы установщиков. Личные файлы пользователя не затрагиваются.",
            EmergencyPlan = "Перед глубокой очисткой системных компонентов Windows создается контрольная точка восстановления.",
            AuditLogSummary = $"Очистка диска {targetDrive}: освобождено {FormatSize(estimatedBytesToFree)}"
        };

        assessment.ImpactDetails.Add($"✓ Временные системные файлы (%TEMP%): очистка отработавших временных файлов программ.");
        assessment.ImpactDetails.Add($"✓ Кэш обновлений Windows Update: удаление загруженных дистрибутивов пакетов.");
        assessment.ImpactDetails.Add($"✓ Дампы памяти и отчеты об ошибках: освобождение пространства после сбоев.");
        assessment.ImpactDetails.Add($"🛡 ГАРАНТИЯ: Папки «Документы», «Рабочий стол», «Загрузки» и реестр остаются нетронутыми.");

        return assessment;
    }

    public static ImpactAssessment AssessFormatting(string driveLetter, string volumeLabel, string fileSystem, long totalBytes)
    {
        var assessment = new ImpactAssessment
        {
            Title = $"Форматирование тома [{driveLetter}]",
            OperationType = OperationImpactType.PartitionFormat,
            TotalBytes = totalBytes,
            TotalSizeFormatted = FormatSize(totalBytes),
            IsReversible = false,
            RiskLevel = "Критический риск (Полное уничтожение данных)",
            RiskBadgeColorHex = "#EF4444",
            TargetSummary = $"Том [{driveLetter}] ({volumeLabel}) • Емкость: {FormatSize(totalBytes)} • Файловая система: {fileSystem}",
            ReversibilityExplanation = "🚨 НЕОБРАТИМО: Форматирование полностью уничтожит файловую таблицу и все существующие на томе файлы и папки!",
            EmergencyPlan = "Обязательно скопируйте все важные данные с тома на другой физический накопитель перед подтверждением.",
            AuditLogSummary = $"Форматирование тома {driveLetter} ({fileSystem})"
        };

        assessment.ImpactDetails.Add($"❌ Все файлы и каталоги на диске [{driveLetter}] будут безвозвратно удалены.");
        assessment.ImpactDetails.Add($"❌ Все установленные на этот том программы и игры перестанут запускаться.");
        assessment.ImpactDetails.Add($"✓ Будет создана новая чистая файловая таблица {fileSystem} с оптимизированным размером кластера.");

        // Проверяем, есть ли там папки
        try
        {
            if (Directory.Exists(driveLetter))
            {
                var topDirs = Directory.GetDirectories(driveLetter).Select(Path.GetFileName).Take(6).ToList();
                if (topDirs.Count > 0)
                {
                    assessment.ImpactDetails.Add($"⚠️ На диске обнаружены папки первого уровня: {string.Join(", ", topDirs)}");
                }
            }
        }
        catch { }

        return assessment;
    }

    public static void LogAudit(string action, string details, bool success)
    {
        try
        {
            string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{(success ? "SUCCESS" : "FAILED")}] {action}: {details}{Environment.NewLine}";
            File.AppendAllText(AuditLogPath, entry);
        }
        catch { }
    }

    public static void RememberDeletedPaths(IEnumerable<string> paths)
    {
        lock (_lastDeletedPaths)
        {
            _lastDeletedPaths.Clear();
            _lastDeletedPaths.AddRange(paths);
        }
    }

    public static List<string> GetLastDeletedPaths()
    {
        lock (_lastDeletedPaths)
        {
            return new List<string>(_lastDeletedPaths);
        }
    }

    public static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024L * 1024L) return $"{bytes / (1024.0 * 1024.0 * 1024.0):F1} ГБ";
        if (bytes >= 1024L * 1024L) return $"{bytes / (1024.0 * 1024.0):F1} МБ";
        if (bytes >= 1024L) return $"{bytes / 1024.0:F1} КБ";
        return $"{bytes} Б";
    }
}
