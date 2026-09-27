using System.IO;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
using SharpCompress.Writers;
using Win11CopyDialog.Modules.ArchiveEngine.Models;
using Win11CopyDialog.Modules.FileManager.Models;

namespace Win11CopyDialog.Modules.ArchiveEngine.Services;

/// <summary>
/// Единый сервис работы с архивами: чтение 13 форматов (7z, zip, rar, tar, gz, bz2, xz, iso, cab, arj, lzh, z, cpio),
/// создание 6 форматов (7z, zip, tar, tar.gz, tar.bz2, tar.xz), потоковая компрессия/декомпрессия,
/// шифрование паролем, проверка целостности без извлечения на диск.
/// </summary>
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
    /// Получить список элементов архива для виртуального просмотра без распаковки.
    /// </summary>
    public static List<FileSystemItem> ReadArchiveEntries(string archivePath, string? password = null)
    {
        var result = new List<FileSystemItem>();
        if (!File.Exists(archivePath)) return result;

        var opt = new ReaderOptions { Password = password };
        using var archive = ArchiveFactory.OpenArchive(archivePath, opt);

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Key)) continue;

            string key = entry.Key.Replace('/', '\\').TrimStart('\\');
            string name = Path.GetFileName(key);
            if (string.IsNullOrEmpty(name)) name = key;

            result.Add(new FileSystemItem
            {
                Name = name,
                FullPath = key,
                Extension = Path.GetExtension(name),
                Length = entry.Size,
                PackedLength = entry.CompressedSize,
                IsDirectory = entry.IsDirectory,
                IsArchive = false,
                LastModifiedTime = entry.LastModifiedTime ?? DateTime.Now,
                CrcHex = entry.Crc != 0 ? $"{entry.Crc:X8}" : "—"
            });
        }

        return result;
    }

    /// <summary>
    /// Создание архива с потоковой телеметрией степени сжатия и скорости.
    /// </summary>
    public static async Task CompressAsync(
        IEnumerable<string> sourcePaths,
        string destinationArchive,
        ArchiveFormat format,
        CompressionLevelPreset levelPreset,
        string? password = null,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            var filesToPack = CollectFilesToPack(sourcePaths);
            long totalBytes = filesToPack.Sum(f => f.length);
            long processedBytes = 0;

            var gate = new ProgressThrottle(progress, ct);

            var compType = ResolveCompressionType(format, levelPreset);
            string? dir = Path.GetDirectoryName(destinationArchive);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using var outStream = File.Create(destinationArchive);
            var writerType = ResolveWriterType(format);

            var writerOptions = new WriterOptions(compType);
            using var writer = WriterFactory.OpenWriter(outStream, writerType, writerOptions);

            DateTime startTime = DateTime.Now;
            string currentFile = string.Empty;
            double lastSpeed = 0;

            foreach (var (fullPath, relativePath, length) in filesToPack)
            {
                ct.ThrowIfCancellationRequested();
                currentFile = Path.GetFileName(fullPath);

                using var inStream = File.OpenRead(fullPath);
                writer.Write(relativePath, inStream, null);

                processedBytes += length;

                double sec = (DateTime.Now - startTime).TotalSeconds;
                lastSpeed = sec > 0.1 ? processedBytes / sec : 0;

                // Троттлинг внутри gate: наружу уходит не чаще 10 раз в секунду.
                gate.Report(currentFile, processedBytes, totalBytes, outStream.Position, lastSpeed);
            }

            // Финальный отчёт принудительный: UI обязан закончить на 100 %,
            // даже если весь архив упаковался быстрее одного интервала.
            gate.ReportFinal(currentFile, totalBytes, totalBytes, outStream.Position, lastSpeed);
        }, ct);
    }

    /// <summary>
    /// Распаковка архива в целевую директорию с потоковой телеметрией.
    /// </summary>
    public static async Task ExtractAsync(
        string archivePath,
        string destinationDirectory,
        IReadOnlyList<string>? specificKeys = null,
        string? password = null,
        bool overwrite = true,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            Directory.CreateDirectory(destinationDirectory);
            var opt = new ReaderOptions { Password = password };
            using var archive = ArchiveFactory.OpenArchive(archivePath, opt);

            long totalBytes = archive.Entries.Where(e => !e.IsDirectory).Sum(e => e.Size);
            long processedBytes = 0;
            DateTime startTime = DateTime.Now;

            var gate = new ProgressThrottle(progress, ct);

            var specificSet = specificKeys != null ? new HashSet<string>(specificKeys, StringComparer.OrdinalIgnoreCase) : null;

            string currentFile = string.Empty;
            long lastCompressed = 0;
            double lastSpeed = 0;

            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.IsDirectory || string.IsNullOrEmpty(entry.Key)) continue;

                string normKey = entry.Key.Replace('/', '\\').TrimStart('\\');
                if (specificSet != null && !specificSet.Contains(entry.Key) && !specificSet.Contains(normKey))
                    continue;

                currentFile = Path.GetFileName(normKey);
                lastCompressed = entry.CompressedSize;

                entry.WriteToDirectory(destinationDirectory, new ExtractionOptions { Overwrite = overwrite, ExtractFullPath = true });

                processedBytes += entry.Size;

                double sec = (DateTime.Now - startTime).TotalSeconds;
                lastSpeed = sec > 0.1 ? processedBytes / sec : 0;

                gate.Report(currentFile, processedBytes, totalBytes, lastCompressed, lastSpeed);
            }

            // Финальный отчёт принудительный — иначе полоса могла бы остаться
            // на 99 % из-за подавления последнего отчёта троттлингом.
            gate.ReportFinal(currentFile, totalBytes, totalBytes, lastCompressed, lastSpeed);
        }, ct);
    }

    /// <summary>
    /// Тестирование целостности архива без извлечения на диск.
    /// </summary>
    public static async Task<bool> TestArchiveIntegrityAsync(
        string archivePath,
        string? password = null,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                var opt = new ReaderOptions { Password = password };
                using var archive = ArchiveFactory.OpenArchive(archivePath, opt);
                long totalBytes = archive.Entries.Where(e => !e.IsDirectory).Sum(e => e.Size);
                long processed = 0;

                var gate = new ProgressThrottle(progress, ct);

                string currentFile = string.Empty;

                foreach (var entry in archive.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (entry.IsDirectory || string.IsNullOrEmpty(entry.Key)) continue;
                    currentFile = Path.GetFileName(entry.Key);

                    using var s = entry.OpenEntryStream();
                    s.CopyTo(Stream.Null);

                    processed += entry.Size;
                    gate.Report(currentFile, processed, totalBytes, entry.CompressedSize, 0);
                }

                // Принудительный финал: полоса проверки доходит до 100 %.
                gate.ReportFinal(currentFile, totalBytes, totalBytes, 0, 0);
                return true;
            }
            catch
            {
                return false;
            }
        }, ct);
    }

    /// <summary>
    /// Гейт отчётности прогресса: наружу уходит НОВЫЙ неизменяемый снимок
    /// не чаще ~10 раз в секунду.
    /// Раньше в UI отправлялся ОДИН И ТОТ ЖЕ экземпляр ArchiveProgress, который
    /// воркер продолжал мутировать после Report(): UI-поток читал поля объекта
    /// «на лету» и получал разорванные пары «файл / размер». Плюс отчёт был на
    /// каждый файл, и архив на 50 000 файлов забивал очередь Dispatcher.
    /// </summary>
    private sealed class ProgressThrottle
    {
        private const long IntervalMs = 100; // ~10 Гц, как в WizTreeAnalyzerWindow

        private readonly IProgress<ArchiveProgress>? _progress;
        private readonly CancellationToken _ct;
        private long _lastReportMs;

        public ProgressThrottle(IProgress<ArchiveProgress>? progress, CancellationToken ct)
        {
            _progress = progress;
            _ct = ct;
        }

        /// <summary>Публикует снимок, если с прошлого раза прошло >= 100 мс.</summary>
        public void Report(string currentFile, long bytesProcessed, long totalBytes, long compressedBytes, double speedBytesPerSec)
        {
            if (!ShouldReport()) return;
            _progress!.Report(Snapshot(currentFile, bytesProcessed, totalBytes, compressedBytes, speedBytesPerSec));
        }

        /// <summary>Финальный снимок — игнорирует троттлинг.</summary>
        public void ReportFinal(string currentFile, long bytesProcessed, long totalBytes, long compressedBytes, double speedBytesPerSec)
        {
            if (_progress == null) return;
            _progress.Report(Snapshot(currentFile, bytesProcessed, totalBytes, compressedBytes, speedBytesPerSec));
        }

        private bool ShouldReport()
        {
            if (_progress == null) return false;
            // После отмены очередь UI не пополняем.
            if (_ct.IsCancellationRequested) return false;

            long now = Environment.TickCount64;
            if (now - _lastReportMs < IntervalMs) return false;
            _lastReportMs = now;
            return true;
        }

        private static ArchiveProgress Snapshot(string currentFile, long bytesProcessed, long totalBytes, long compressedBytes, double speedBytesPerSec)
        {
            // Новая сущность на каждый отчёт: её больше никто не меняет,
            // поэтому UI не может прочитать «половину» обновления.
            return new ArchiveProgress
            {
                CurrentFile = currentFile,
                BytesProcessed = bytesProcessed,
                TotalBytes = totalBytes,
                CompressedBytes = compressedBytes,
                CurrentSpeedBytesPerSec = speedBytesPerSec
            };
        }
    }

    private static List<(string fullPath, string relativePath, long length)> CollectFilesToPack(IEnumerable<string> sourcePaths)
    {
        var list = new List<(string, string, long)>();
        foreach (var path in sourcePaths)
        {
            if (File.Exists(path))
            {
                var fi = new FileInfo(path);
                list.Add((fi.FullName, fi.Name, fi.Length));
            }
            else if (Directory.Exists(path))
            {
                var baseDir = new DirectoryInfo(path);
                string parent = baseDir.Parent != null ? baseDir.Parent.FullName : baseDir.FullName;
                foreach (var f in baseDir.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(parent, f.FullName);
                    list.Add((f.FullName, rel, f.Length));
                }
            }
        }
        return list;
    }

    private static CompressionType ResolveCompressionType(ArchiveFormat format, CompressionLevelPreset preset)
    {
        if (preset == CompressionLevelPreset.Store) return CompressionType.None;

        return format switch
        {
            ArchiveFormat.Zip => CompressionType.Deflate,
            ArchiveFormat.SevenZip => CompressionType.LZMA,
            ArchiveFormat.TarGz => CompressionType.GZip,
            ArchiveFormat.TarBz2 => CompressionType.BZip2,
            ArchiveFormat.TarXz => CompressionType.None,
            _ => CompressionType.Deflate
        };
    }

    private static ArchiveType ResolveWriterType(ArchiveFormat format)
    {
        return format switch
        {
            ArchiveFormat.Zip => ArchiveType.Zip,
            ArchiveFormat.SevenZip => ArchiveType.SevenZip,
            ArchiveFormat.Tar or ArchiveFormat.TarGz or ArchiveFormat.TarBz2 or ArchiveFormat.TarXz => ArchiveType.Tar,
            _ => ArchiveType.Zip
        };
    }
}
