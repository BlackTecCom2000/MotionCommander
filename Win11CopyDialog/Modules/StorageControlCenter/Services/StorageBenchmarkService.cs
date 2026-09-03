using System.Diagnostics;
using System.IO;
using Win11CopyDialog.Modules.StorageControlCenter.Models;

namespace Win11CopyDialog.Modules.StorageControlCenter.Services;

public static class StorageBenchmarkService
{
    public static async Task<StorageBenchmarkSessionResult> RunBenchmarkAsync(
        BenchmarkConfig config,
        IProgress<(string testName, int percent, double currentSpeed)>? progress = null,
        CancellationToken ct = default)
    {
        var session = new StorageBenchmarkSessionResult
        {
            DriveLetter = config.TargetDrive
        };

        string tempFolder = Path.Combine(config.TargetDrive, $"__mc_bench_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempFolder);
        string benchFile = Path.Combine(tempFolder, "bench.dat");

        try
        {
            // 1. Sequential Write (SEQ1M Q8T1)
            var seqWrite = new StorageBenchmarkItem
            {
                TestType = "Последовательная запись (Seq Write)",
                BlockSize = "1 МБ",
                QueueThreads = "Q8T1",
                Status = "Тестирование..."
            };
            session.Items.Add(seqWrite);
            progress?.Report(("Последовательная запись (1 МБ)...", 10, 0));

            seqWrite.WriteSpeedMBps = await RunSequentialWriteAsync(benchFile, config.FileSizeBytes, 1024 * 1024, ct);
            seqWrite.WriteIops = seqWrite.WriteSpeedMBps * 1024 * 1024 / (1024 * 1024);
            seqWrite.Status = "Завершено";

            // 2. Sequential Read (SEQ1M Q8T1)
            var seqRead = new StorageBenchmarkItem
            {
                TestType = "Последовательное чтение (Seq Read)",
                BlockSize = "1 МБ",
                QueueThreads = "Q8T1",
                Status = "Тестирование..."
            };
            session.Items.Add(seqRead);
            progress?.Report(("Последовательное чтение (1 МБ)...", 35, seqWrite.WriteSpeedMBps));

            seqRead.ReadSpeedMBps = await RunSequentialReadAsync(benchFile, 1024 * 1024, ct);
            seqRead.ReadIops = seqRead.ReadSpeedMBps * 1024 * 1024 / (1024 * 1024);
            seqRead.Status = "Завершено";

            // 3. Random 4K Read (RND4K Q32T1)
            var rnd4kRead = new StorageBenchmarkItem
            {
                TestType = "Случайное чтение 4K (Rnd 4K)",
                BlockSize = "4 КБ",
                QueueThreads = "Q32T1",
                Status = "Тестирование..."
            };
            session.Items.Add(rnd4kRead);
            progress?.Report(("Случайное чтение 4K (IOPS)...", 60, seqRead.ReadSpeedMBps));

            (rnd4kRead.ReadSpeedMBps, rnd4kRead.ReadIops, rnd4kRead.ReadLatencyUs) = await RunRandom4KReadAsync(benchFile, 4000, ct);
            rnd4kRead.Status = "Завершено";

            // 4. Random 4K Write (RND4K Q32T1)
            var rnd4kWrite = new StorageBenchmarkItem
            {
                TestType = "Случайная запись 4K (Rnd 4K)",
                BlockSize = "4 КБ",
                QueueThreads = "Q32T1",
                Status = "Тестирование..."
            };
            session.Items.Add(rnd4kWrite);
            progress?.Report(("Случайная запись 4K (IOPS)...", 85, rnd4kRead.ReadSpeedMBps));

            (rnd4kWrite.WriteSpeedMBps, rnd4kWrite.WriteIops, rnd4kWrite.WriteLatencyUs) = await RunRandom4KWriteAsync(benchFile, 2000, ct);
            rnd4kWrite.Status = "Завершено";

            // Расчет рейтинга производительности
            double avgLinear = (seqRead.ReadSpeedMBps + seqWrite.WriteSpeedMBps) / 2.0;
            double avgIops = (rnd4kRead.ReadIops + rnd4kWrite.WriteIops) / 2.0;
            session.OverallPerformanceScore = Math.Round(avgLinear + avgIops / 15.0, 1);

            progress?.Report(("Бенчмарк успешно завершен", 100, avgLinear));
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, true);
            }
            catch { }
        }

        return session;
    }

    private static async Task<double> RunSequentialWriteAsync(string filePath, int totalBytes, int bufferSize, CancellationToken ct)
    {
        byte[] buffer = new byte[bufferSize];
        new Random(42).NextBytes(buffer);

        var sw = Stopwatch.StartNew();
        long written = 0;

        await using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, FileOptions.WriteThrough | FileOptions.Asynchronous))
        {
            while (written < totalBytes)
            {
                ct.ThrowIfCancellationRequested();
                int toWrite = (int)Math.Min(bufferSize, totalBytes - written);
                await fs.WriteAsync(buffer.AsMemory(0, toWrite), ct);
                written += toWrite;
            }
            await fs.FlushAsync(ct);
        }

        sw.Stop();
        double seconds = Math.Max(0.001, sw.Elapsed.TotalSeconds);
        return Math.Round(written / (1024.0 * 1024.0) / seconds, 1);
    }

    private static async Task<double> RunSequentialReadAsync(string filePath, int bufferSize, CancellationToken ct)
    {
        byte[] buffer = new byte[bufferSize];
        var sw = Stopwatch.StartNew();
        long readTotal = 0;

        await using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous))
        {
            int read;
            while ((read = await fs.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                readTotal += read;
            }
        }

        sw.Stop();
        double seconds = Math.Max(0.001, sw.Elapsed.TotalSeconds);
        return Math.Round(readTotal / (1024.0 * 1024.0) / seconds, 1);
    }

    private static async Task<(double mbps, double iops, double latencyUs)> RunRandom4KReadAsync(string filePath, int opsCount, CancellationToken ct)
    {
        long fileSize = new FileInfo(filePath).Length;
        if (fileSize < 4096) return (0, 0, 0);

        int maxBlocks = (int)(fileSize / 4096);
        byte[] buffer = new byte[4096];
        var rng = new Random(1337);

        var sw = Stopwatch.StartNew();
        long readBytes = 0;

        await using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess | FileOptions.Asynchronous))
        {
            for (int i = 0; i < opsCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                long offset = (long)rng.Next(0, maxBlocks) * 4096;
                fs.Seek(offset, SeekOrigin.Begin);
                int r = await fs.ReadAsync(buffer.AsMemory(0, 4096), ct);
                readBytes += r;
            }
        }

        sw.Stop();
        double sec = Math.Max(0.0001, sw.Elapsed.TotalSeconds);
        double mbps = Math.Round(readBytes / (1024.0 * 1024.0) / sec, 1);
        double iops = Math.Round(opsCount / sec);
        double latencyUs = Math.Round((sec / opsCount) * 1_000_000.0, 1);

        return (mbps, iops, latencyUs);
    }

    private static async Task<(double mbps, double iops, double latencyUs)> RunRandom4KWriteAsync(string filePath, int opsCount, CancellationToken ct)
    {
        long fileSize = new FileInfo(filePath).Length;
        if (fileSize < 4096) return (0, 0, 0);

        int maxBlocks = (int)(fileSize / 4096);
        byte[] buffer = new byte[4096];
        new Random(777).NextBytes(buffer);
        var rng = new Random(1337);

        var sw = Stopwatch.StartNew();
        long writtenBytes = 0;

        await using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.None, 4096, FileOptions.RandomAccess | FileOptions.WriteThrough | FileOptions.Asynchronous))
        {
            for (int i = 0; i < opsCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                long offset = (long)rng.Next(0, maxBlocks) * 4096;
                fs.Seek(offset, SeekOrigin.Begin);
                await fs.WriteAsync(buffer.AsMemory(0, 4096), ct);
                writtenBytes += 4096;
            }
            await fs.FlushAsync(ct);
        }

        sw.Stop();
        double sec = Math.Max(0.0001, sw.Elapsed.TotalSeconds);
        double mbps = Math.Round(writtenBytes / (1024.0 * 1024.0) / sec, 1);
        double iops = Math.Round(opsCount / sec);
        double latencyUs = Math.Round((sec / opsCount) * 1_000_000.0, 1);

        return (mbps, iops, latencyUs);
    }
}
