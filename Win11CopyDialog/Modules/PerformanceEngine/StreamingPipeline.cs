using System.Diagnostics;
using System.IO;
using System.Threading.Channels;

namespace Win11CopyDialog.Modules.PerformanceEngine;

public sealed class PipelineBlock
{
    public PooledBuffer Buffer { get; }
    public int Count { get; set; }

    public PipelineBlock(PooledBuffer buffer, int count)
    {
        Buffer = buffer;
        Count = count;
    }
}

public sealed class PipelineTelemetry
{
    public long BytesTransferred { get; set; }
    public double InstantThroughputBytesPerSec { get; set; }
    public double ReadLatencyMs { get; set; }
    public double WriteLatencyMs { get; set; }
    public int QueueDepth { get; set; }
}

/// <summary>
/// Высокопроизводительный двухбуферный конвейер потокового ввода/вывода (Full Duplex).
/// Читает следующий блок данных с источника асинхронно, пока предыдущий записывается на приёмник.
/// </summary>
public static class StreamingPipeline
{
    public static async Task CopyStreamPipelineAsync(
        string sourcePath,
        string destPath,
        int bufferSize,
        Action<PipelineTelemetry>? onTelemetry = null,
        ManualResetEventSlim? pauseGate = null,
        CancellationToken ct = default)
    {
        string? destDir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

        const FileOptions readOptions = FileOptions.Asynchronous | FileOptions.SequentialScan;
        const FileOptions writeOptions = FileOptions.Asynchronous;

        // Открытие асинхронных файловых потоков
        using var srcStream = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize,
            readOptions);

        using var dstStream = new FileStream(
            destPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize,
            writeOptions);

        // Канал глубиной 2 для обеспечения двухбуферного перекрывающегося I/O (double buffering)
        var channel = Channel.CreateBounded<PipelineBlock>(new BoundedChannelOptions(2)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        long totalCopied = 0;
        double currentReadLatency = 0;
        double currentWriteLatency = 0;

        // Поток-читатель (Producer)
        var readerTask = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (pauseGate != null && !pauseGate.IsSet)
                    {
                        pauseGate.Wait(ct);
                    }

                    var pBuffer = BufferPool.Rent(bufferSize);
                    long readStart = Stopwatch.GetTimestamp();

                    int read = await srcStream.ReadAsync(pBuffer.Memory, ct);
                    double elapsedMs = (Stopwatch.GetTimestamp() - readStart) * 1000.0 / Stopwatch.Frequency;
                    currentReadLatency = elapsedMs;

                    if (read <= 0)
                    {
                        pBuffer.Dispose();
                        break;
                    }

                    var block = new PipelineBlock(pBuffer, read);
                    await channel.Writer.WriteAsync(block, ct);
                }
            }
            finally
            {
                channel.Writer.Complete();
            }
        }, ct);

        // Поток-писатель (Consumer)
        var writerTask = Task.Run(async () =>
        {
            long lastReportTime = Stopwatch.GetTimestamp();
            long bytesSinceReport = 0;

            await foreach (var block in channel.Reader.ReadAllAsync(ct))
            {
                using (block.Buffer)
                {
                    if (pauseGate != null && !pauseGate.IsSet)
                    {
                        pauseGate.Wait(ct);
                    }

                    long writeStart = Stopwatch.GetTimestamp();
                    await dstStream.WriteAsync(block.Buffer.Memory.Slice(0, block.Count), ct);
                    double elapsedMs = (Stopwatch.GetTimestamp() - writeStart) * 1000.0 / Stopwatch.Frequency;
                    currentWriteLatency = elapsedMs;

                    totalCopied += block.Count;
                    bytesSinceReport += block.Count;

                    long now = Stopwatch.GetTimestamp();
                    double intervalSec = (now - lastReportTime) / (double)Stopwatch.Frequency;
                    if (intervalSec >= 0.1) // Каждые 100 мс передаем телеметрию
                    {
                        double speed = bytesSinceReport / intervalSec;
                        bytesSinceReport = 0;
                        lastReportTime = now;

                        onTelemetry?.Invoke(new PipelineTelemetry
                        {
                            BytesTransferred = totalCopied,
                            InstantThroughputBytesPerSec = speed,
                            ReadLatencyMs = currentReadLatency,
                            WriteLatencyMs = currentWriteLatency,
                            QueueDepth = channel.Reader.Count
                        });
                    }
                }
            }

            // Финальный сброс телеметрии
            onTelemetry?.Invoke(new PipelineTelemetry
            {
                BytesTransferred = totalCopied,
                InstantThroughputBytesPerSec = 0,
                ReadLatencyMs = currentReadLatency,
                WriteLatencyMs = currentWriteLatency,
                QueueDepth = 0
            });
        }, ct);

        await Task.WhenAll(readerTask, writerTask);

        // Сохранение временных меток и атрибутов исходного файла
        try
        {
            File.SetLastWriteTime(destPath, File.GetLastWriteTime(sourcePath));
            File.SetCreationTime(destPath, File.GetCreationTime(sourcePath));
        }
        catch { }
    }
}
