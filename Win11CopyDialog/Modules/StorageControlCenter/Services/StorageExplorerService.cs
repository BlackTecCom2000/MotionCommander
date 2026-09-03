using System.IO;
using Win11CopyDialog.Helpers;
using Win11CopyDialog.Modules.StorageControlCenter.Models;

namespace Win11CopyDialog.Modules.StorageControlCenter.Services;

public sealed class StorageCategoryBreakdown
{
    public string CategoryName { get; set; } = "";
    public string ColorHex { get; set; } = "#3B82F6";
    public long SizeBytes { get; set; }
    public double Percent { get; set; }

    public string SizeFormatted => Formatters.Bytes(SizeBytes);
    public string PercentFormatted => $"{Percent:F1}%";
}

public static class StorageExplorerService
{
    public static async Task<(List<LargeFileItem> largeFiles, List<StorageCategoryBreakdown> categories)> AnalyzeStorageUsageAsync(
        string rootPath,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var largeFiles = new List<LargeFileItem>();
            long mediaBytes = 0;
            long archiveBytes = 0;
            long docBytes = 0;
            long appBytes = 0;
            long otherBytes = 0;
            long totalScanned = 0;

            if (!Directory.Exists(rootPath)) return (largeFiles, new List<StorageCategoryBreakdown>());

            progress?.Report($"Сканирование структуры каталогов {rootPath}...");

            try
            {
                var di = new DirectoryInfo(rootPath);
                foreach (var f in di.EnumerateFiles("*", new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, MaxRecursionDepth = 4 }))
                {
                    if (ct.IsCancellationRequested) break;

                    long len = f.Length;
                    totalScanned += len;
                    string ext = f.Extension.ToLowerInvariant();

                    if (ext is ".mp4" or ".mkv" or ".avi" or ".mov" or ".mp3" or ".wav" or ".flac" or ".png" or ".jpg" or ".jpeg" or ".webp")
                        mediaBytes += len;
                    else if (ext is ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".iso" or ".zst")
                        archiveBytes += len;
                    else if (ext is ".exe" or ".dll" or ".msi" or ".sys")
                        appBytes += len;
                    else if (ext is ".pdf" or ".docx" or ".xlsx" or ".txt" or ".pptx" or ".csv" or ".json")
                        docBytes += len;
                    else
                        otherBytes += len;

                    // Крупные файлы (> 250 МБ)
                    if (len > 250L * 1024 * 1024)
                    {
                        largeFiles.Add(new LargeFileItem
                        {
                            FilePath = f.FullName,
                            FileName = f.Name,
                            Extension = ext,
                            SizeBytes = len,
                            LastModified = f.LastWriteTime
                        });
                    }
                }
            }
            catch { }

            largeFiles.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
            if (largeFiles.Count > 30) largeFiles = largeFiles.Take(30).ToList();

            long total = Math.Max(1, totalScanned);
            var categories = new List<StorageCategoryBreakdown>
            {
                new() { CategoryName = "Медиа (Видео, Музыка, Фото)", ColorHex = "#8B5CF6", SizeBytes = mediaBytes, Percent = (double)mediaBytes / total * 100.0 },
                new() { CategoryName = "Архивы и Образы (ZIP, 7Z, ISO)", ColorHex = "#3B82F6", SizeBytes = archiveBytes, Percent = (double)archiveBytes / total * 100.0 },
                new() { CategoryName = "Программы и Драйверы (EXE, DLL)", ColorHex = "#10B981", SizeBytes = appBytes, Percent = (double)appBytes / total * 100.0 },
                new() { CategoryName = "Документы (PDF, DOCX, Таблицы)", ColorHex = "#F59E0B", SizeBytes = docBytes, Percent = (double)docBytes / total * 100.0 },
                new() { CategoryName = "Прочие файлы и кэш", ColorHex = "#64748B", SizeBytes = otherBytes, Percent = (double)otherBytes / total * 100.0 }
            };

            return (largeFiles, categories);
        }, ct);
    }
}
