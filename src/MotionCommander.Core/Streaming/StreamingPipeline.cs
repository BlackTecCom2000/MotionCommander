using System.Diagnostics;
using System.IO;
using System.Threading.Channels;

namespace MotionCommander.Core.Streaming;

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

        var channelOptions = new BoundedChannelOptions(2)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        };
        var channel = Channel.CreateBounded<PipelineBlock>(channelOptions);

        long totalBytesWritten = 0;
        var swTotal = Stopwatch.StartNew();
        long lastTimestamp = Stopwatch.GetTimestamp();
        long lastBytes = 0;

        var writerTask = Task.Run(async () =>
        {
            var swWrite = new Stopwatch();
            try
            {
                while (await channel.Reader.WaitToReadAsync(ct))
                {
                    while (channel.Reader.TryRead(out var block))
                    {
                        ct.ThrowIfCancellationRequested();
                        pauseGate?.Wait(ct);

                        swWrite.Restart();
                        await dstStream.WriteAsync(block.Buffer.Data.AsMemory(0, block.Count), ct);
                        swWrite.Stop();

                        totalBytesWritten += block.Count;
                        block.Buffer.Dispose();

                        long currentTs = Stopwatch.GetTimestamp();
                        double elapsedSec = (double)(currentTs - lastTimestamp) / Stopwatch.Frequency;
                        if (elapsedSec >= 0.05)
                        {
                            long deltaBytes = totalBytesWritten - lastBytes;
                            double instantSpeed = deltaBytes / elapsedSec;
                            lastTimestamp = currentTs;
                            lastBytes = totalBytesWritten;

                            onTelemetry?.Invoke(new PipelineTelemetry
                            {
                                BytesTransferred = totalBytesWritten,
                                InstantThroughputBytesPerSec = instantSpeed,
                                WriteLatencyMs = swWrite.Elapsed.TotalMilliseconds,
                                QueueDepth = channel.Reader.Count
                            });
                        }
                    }
                }

                await dstStream.FlushAsync(ct);
            }
            finally
            {
                double finalSec = swTotal.Elapsed.TotalSeconds;
                if (finalSec > 0)
                {
                    onTelemetry?.Invoke(new PipelineTelemetry
                    {
                        BytesTransferred = totalBytesWritten,
                        InstantThroughputBytesPerSec = totalBytesWritten / finalSec,
                        QueueDepth = 0
                    });
                }
            }
        }, ct);

        var swRead = new Stopwatch();
        try
        {
            while (srcStream.Position < srcStream.Length)
            {
                ct.ThrowIfCancellationRequested();
                pauseGate?.Wait(ct);

                var pooled = BufferPool.Rent(bufferSize);
                swRead.Restart();
                int bytesRead = await srcStream.ReadAsync(pooled.Data.AsMemory(0, bufferSize), ct);
                swRead.Stop();

                if (bytesRead <= 0)
                {
                    pooled.Dispose();
                    break;
                }

                await channel.Writer.WriteAsync(new PipelineBlock(pooled, bytesRead), ct);
            }
        }
        finally
        {
            channel.Writer.Complete();
        }

        await writerTask;
    }
}
