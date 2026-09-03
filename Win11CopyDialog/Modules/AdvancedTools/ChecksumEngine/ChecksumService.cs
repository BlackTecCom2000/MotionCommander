using System.IO;
using System.Security.Cryptography;

namespace Win11CopyDialog.Modules.AdvancedTools.ChecksumEngine;

public sealed class ChecksumResult
{
    public string Crc32 { get; set; } = "";
    public string Md5 { get; set; } = "";
    public string Sha1 { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Sha512 { get; set; } = "";
    public long FileSizeBytes { get; set; }
    public double ElapsedSeconds { get; set; }
}

public static class ChecksumService
{
    private static readonly uint[] CrcTable = InitializeCrcTable();

    private static uint[] InitializeCrcTable()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 8; j > 0; j--)
            {
                if ((crc & 1) == 1)
                    crc = (crc >> 1) ^ 0xEDB88320;
                else
                    crc >>= 1;
            }
            table[i] = crc;
        }
        return table;
    }

    /// <summary>
    /// Вычисляет все хэш-суммы за один потоковый проход без загрузки файла в ОЗУ.
    /// </summary>
    public static async Task<ChecksumResult> ComputeHashesAsync(
        string filePath,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var result = new ChecksumResult();
            var fi = new FileInfo(filePath);
            result.FileSizeBytes = fi.Length;

            using var md5 = MD5.Create();
            using var sha1 = SHA1.Create();
            using var sha256 = SHA256.Create();
            using var sha512 = SHA512.Create();

            uint crc = 0xFFFFFFFF;
            byte[] buffer = new byte[128 * 1024]; // 128 KB
            long totalRead = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            using var fs = File.OpenRead(filePath);
            int bytesRead;

            while ((bytesRead = fs.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();

                // CRC32
                for (int i = 0; i < bytesRead; i++)
                {
                    byte b = buffer[i];
                    crc = (crc >> 8) ^ CrcTable[(crc & 0xFF) ^ b];
                }

                // Квантовые крипто-хэши
                md5.TransformBlock(buffer, 0, bytesRead, null, 0);
                sha1.TransformBlock(buffer, 0, bytesRead, null, 0);
                sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
                sha512.TransformBlock(buffer, 0, bytesRead, null, 0);

                totalRead += bytesRead;
                if (result.FileSizeBytes > 0)
                {
                    progress?.Report((double)totalRead / result.FileSizeBytes * 100.0);
                }
            }

            // Финализация
            md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            sha1.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            sha512.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

            sw.Stop();
            result.ElapsedSeconds = sw.Elapsed.TotalSeconds;

            result.Crc32 = $"{~crc:X8}";
            result.Md5 = Convert.ToHexString(md5.Hash ?? Array.Empty<byte>());
            result.Sha1 = Convert.ToHexString(sha1.Hash ?? Array.Empty<byte>());
            result.Sha256 = Convert.ToHexString(sha256.Hash ?? Array.Empty<byte>());
            result.Sha512 = Convert.ToHexString(sha512.Hash ?? Array.Empty<byte>());

            return result;
        }, ct);
    }

    /// <summary>
    /// Побайтовое бинарное сравнение двух файлов.
    /// </summary>
    public static async Task<(bool areEqual, long diffOffset, string message)> CompareBinaryAsync(
        string path1,
        string path2,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var f1 = new FileInfo(path1);
            var f2 = new FileInfo(path2);

            if (f1.Length != f2.Length)
            {
                return (false, -1, $"Размеры файлов отличаются: {f1.Length} байт vs {f2.Length} байт.");
            }

            byte[] b1 = new byte[128 * 1024];
            byte[] b2 = new byte[128 * 1024];

            using var s1 = File.OpenRead(path1);
            using var s2 = File.OpenRead(path2);

            long total = 0;
            int r1;
            while ((r1 = s1.Read(b1, 0, b1.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                int r2 = s2.Read(b2, 0, r1);
                if (r1 != r2)
                {
                    return (false, total, "Несоответствие потоков при чтении.");
                }

                for (int i = 0; i < r1; i++)
                {
                    if (b1[i] != b2[i])
                    {
                        return (false, total + i, $"Первое несовпадение по смещению 0x{(total + i):X8} ({total + i} байт).");
                    }
                }

                total += r1;
                if (f1.Length > 0) progress?.Report((double)total / f1.Length * 100.0);
            }

            return (true, -1, "Файлы абсолютно идентичны на бинарном уровне.");
        }, ct);
    }
}
