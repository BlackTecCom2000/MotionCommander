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

        await Task.Run(() =>
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath, opt);
            var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
            long totalBytes = entries.Sum(e => e.Size);
            long extractedBytes = 0;

            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                entry.WriteToDirectory(destinationDir, new ExtractionOptions
                {
                    ExtractFullPath = true,
                    Overwrite = true
                });

                extractedBytes += entry.Size;
                if (progress != null && totalBytes > 0)
                {
                    progress.Report(new ArchiveProgress
                    {
                        CurrentEntryName = entry.Key ?? "",
                        CurrentBytes = extractedBytes,
                        TotalBytes = totalBytes,
                        Percent = (int)((extractedBytes * 100) / totalBytes)
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
