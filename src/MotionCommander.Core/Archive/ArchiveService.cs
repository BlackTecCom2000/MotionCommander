using System.IO;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Archives.Tar;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace MotionCommander.Core.Archive;

public static class ArchiveService
{
    private static readonly HashSet<string> ReadExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".tar", ".gz", ".bz2", ".xz", ".tgz", ".tbz2", ".txz",
        ".iso", ".cab", ".arj", ".lzh", ".z", ".cpio"
    };

    public static bool IsArchive(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ReadExtensions.Contains(ext);
    }

    /// <summary>
    /// Защита от Zip Slip (Path Traversal).
    ///
    /// Запись в архиве вида «../../Windows/System32/evil.dll» в сочетании с
    /// ExtractFullPath = true приводит к записи файлов ЗА ПРЕДЕЛАМИ папки
    /// назначения. Такие архивы встречаются в природе и являются приёмом
    /// Path Traversal, поэтому путь каждой записи обязан быть проверен.
    ///
    /// Возвращает безопасный абсолютный путь либо null, если запись опасна.
    /// </summary>
    private static string? ResolveSafePath(string destinationDir, string entryKey)
    {
        // Нормализуем разделители и отсекаем ведущие корневые символы.
        string normalized = entryKey.Replace('\\', '/').TrimStart('/');

        // Отклоняем пути с сегментами «..» ДО любых преобразований.
        if (normalized.Split('/').Any(seg => seg == "..")) return null;

        string rootFull = Path.GetFullPath(destinationDir);
        if (!rootFull.EndsWith(Path.DirectorySeparatorChar))
            rootFull += Path.DirectorySeparatorChar;

        string combined = Path.GetFullPath(Path.Combine(destinationDir, normalized));

        // Итоговая проверка: результат обязан находиться внутри корня.
        if (!combined.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return null;

        return combined;
    }

    public static List<ArchiveItem> ListEntries(string archivePath, string? password = null)
    {
        var result = new List<ArchiveItem>();
        if (!File.Exists(archivePath)) return result;

        var opt = new ReaderOptions { Password = password };
        using var archive = ArchiveFactory.OpenArchive(archivePath, opt);

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Key)) continue;

            string key = entry.Key.Replace('\\', '/').TrimStart('/');
            string name = Path.GetFileName(key);
            if (string.IsNullOrEmpty(name)) name = key;

            result.Add(new ArchiveItem
            {
                Name = name,
                FullPath = key,
                IsDirectory = entry.IsDirectory,
                Size = entry.Size,
                CompressedSize = entry.CompressedSize,
                LastModified = entry.LastModifiedTime ?? DateTime.Now
            });
        }

        return result;
    }

    public static async Task ExtractAllAsync(
        string archivePath,
        string destinationDir,
        string? password = null,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(destinationDir);
        var opt = new ReaderOptions { Password = password };

        await Task.Run(async () =>
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath, opt);
            var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
            long totalBytes = entries.Sum(e => e.Size);
            long extractedBytes = 0;

            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();

                // Защита от Zip Slip: запись с путём вне папки назначения
                // отбрасывается, а не распаковывается поверх системных файлов.
                string? safePath = ResolveSafePath(destinationDir, entry.Key ?? "");
                if (safePath == null)
                {
                    throw new InvalidDataException(
                        $"Обнаружена небезопасная запись в архиве, распаковка прервана: \"{entry.Key}\"");
                }

                string? parentDir = Path.GetDirectoryName(safePath);
                if (!string.IsNullOrEmpty(parentDir)) Directory.CreateDirectory(parentDir);

                using (var entryStream = entry.OpenEntryStream())
                using (var outStream = new FileStream(safePath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await entryStream.CopyToAsync(outStream, 81920, ct).ConfigureAwait(false);
                }

                extractedBytes += entry.Size;
                if (progress != null && totalBytes > 0)
                {
                    progress.Report(new ArchiveProgress
                    {
                        CurrentEntryName = entry.Key ?? "",
                        CurrentBytes = extractedBytes,
                        TotalBytes = totalBytes,
                        // Math.Clamp защищает ProgressBar.Value от выхода за 100.
                        Percent = (int)Math.Clamp((extractedBytes * 100.0) / totalBytes, 0, 100)
                    });
                }
            }
        }, ct);
    }

    public static async Task CreateZipAsync(
        IEnumerable<string> sourcePaths,
        string destinationZipPath,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken ct = default)
    {
        string? parent = Path.GetDirectoryName(destinationZipPath);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        await Task.Run(() =>
        {
            using var archive = ZipArchive.CreateArchive();
            foreach (var path in sourcePaths)
            {
                ct.ThrowIfCancellationRequested();
                if (File.Exists(path))
                {
                    archive.AddEntry(Path.GetFileName(path), File.OpenRead(path));
                }
                else if (Directory.Exists(path))
                {
                    archive.AddAllFromDirectory(path);
                }
            }
            archive.SaveTo(destinationZipPath, CompressionType.Deflate);
        }, ct);
    }
}
