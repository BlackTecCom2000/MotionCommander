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

        // ── Копирование через временный файл ──────────────────────────────────
        // Раньше здесь стоял FileMode.Create прямо на ИТОГОВОМ пути. Из-за
        // этого отмена или ошибка записи оставляли на диске обрезанный файл
        // ПОД СВОИМ ИМЕНЕМ: пользователь видел movie.mkv «готовое», хотя
        // половина не была записана. Теперь пишем в "<имя>.partial",
        // сверяем длину и только потом атомарно переносим на место.
        string partialPath = destPath + ".partial";
        bool committed = false;

        try
        {
            using var srcStream = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize,
                readOptions);

            long sourceLength = srcStream.Length;

            await using (var dstStream = new FileStream(
                partialPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize,
                writeOptions))
            {
                await RunPipelineAsync(srcStream, dstStream, bufferSize, onTelemetry, pauseGate, ct);

                // Принудительный сброс в ОС: FileStream.Dispose() пишет
                // только в буфер ОС, а не вызывает FlushFileBuffers.
                // Отключение питания сразу после «Готово» оставляло
                // файлы правильной длины, но с недописанным хвостом.
                await dstStream.FlushAsync(ct);
            }

            // Проверка целостности. Раньше её не было вовсе: считалось,
            // что «цикл записи завершился» = «файл скопирован».
            long actual = new FileInfo(partialPath).Length;
            if (actual != sourceLength)
                throw new IOException(
                    $"Копирование неполное: записано {actual} из {sourceLength} байт " +
                    $"({Path.GetFileName(sourcePath)}). Итоговый файл не создан.");

            // Атомарный перенос на место.
            Commit(partialPath, destPath);
            committed = true;

            // Сохранение временных меток исходного файла.
            try
            {
                File.SetLastWriteTime(destPath, File.GetLastWriteTime(sourcePath));
                File.SetCreationTime(destPath, File.GetCreationTime(sourcePath));
            }
            catch { /* метки не критичны для целостности */ }
        }
        finally
        {
            // Временный файл убирается в любом случае, чтобы не оставлять
            // мусор рядом с данными пользователя.
            if (!committed)
            {
                try { if (File.Exists(partialPath)) File.Delete(partialPath); } catch { }
            }
        }
    }

    /// <summary>Переносит проверенный временный файл на итоговый путь.</summary>
    private static void Commit(string partialPath, string destPath)
    {
        if (!File.Exists(destPath))
        {
            File.Move(partialPath, destPath);
            return;
        }

        // Файл назначения уже существует. Раньше он просто затирался
        // (FileMode.Create) без предупреждения — это была безвозвратная
        // потеря данных. Теперь предыдущий файл переименовывается, и
        // пользователь может его найти.
        string backup = destPath + ".replaced-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        try
        {
            File.Move(destPath, backup, overwrite: false);
        }
        catch (IOException)
        {
            // Не смогли освободить имя — не перезаписываем.
            throw new IOException(
                $"Файл «{Path.GetFileName(destPath)}» уже существует и не может быть заменён. " +
                "Переименуйте его вручную либо удалите.");
        }

        try
        {
            File.Move(partialPath, destPath);
        }
        catch
        {
            // Возвращаем на место, чтобы не оставлять пользователя без файла.
            try { File.Move(backup, destPath, overwrite: true); } catch { }
            throw;
        }
    }

    /// <summary>Собственно конвейер чтение→запись с двойной буферизацией.</summary>
    private static async Task RunPipelineAsync(
        Stream srcStream,
        Stream dstStream,
        int bufferSize,
        Action<PipelineTelemetry>? onTelemetry,
        ManualResetEventSlim? pauseGate,
        CancellationToken ct)
    {
        // Канал глубиной 2 для двухбуферного перекрывающегося I/O.
        var channel = Channel.CreateBounded<PipelineBlock>(new BoundedChannelOptions(2)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        long totalCopied = 0;
        double currentReadLatency = 0;
        double currentWriteLatency = 0;

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

                    int read;
                    try
                    {
                        long readStart = Stopwatch.GetTimestamp();
                        read = await srcStream.ReadAsync(pBuffer.Memory, ct);
                        currentReadLatency = (Stopwatch.GetTimestamp() - readStart) * 1000.0 / Stopwatch.Frequency;
                    }
                    catch
                    {
                        // Без этого буфер УТЕКАЛ из пула при каждой отмене
                        // копирования: пул истощался, давление на сборщик
                        // росло, и на больших объёмах это доходило до OOM.
                        pBuffer.Dispose();
                        throw;
                    }

                    if (read <= 0)
                    {
                        pBuffer.Dispose();
                        break;
                    }

                    var block = new PipelineBlock(pBuffer, read);
                    try
                    {
                        await channel.Writer.WriteAsync(block, ct);
                    }
                    catch
                    {
                        block.Buffer.Dispose();
                        throw;
                    }
                }
            }
            catch
            {
                // Ошибка читателя ОБЯЗАНА разблокировать писателя.
                // Раньше здесь стоял только channel.Writer.Complete() в
                    // finally, а Complete() НЕ разблокирует уже ожидающий
                // WriteAsync. При ошибке записи (диск заполнен, USB выдернут)
                // писатель падал, читатель навсегда зависал на
                // WriteAsync, и Task.WhenAll не возвращался — копирование
                // «копировалось» вечно при замороженном проценте.
                channel.Writer.TryComplete();
                throw;
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, ct);

        var writerTask = Task.Run(async () =>
        {
            long lastReportTime = Stopwatch.GetTimestamp();
            long bytesSinceReport = 0;

            try
            {
                await foreach (var block in channel.Reader.ReadAllAsync(ct))
                {
                    using (block.Buffer)
                    {
                        // ПРИНЦИПИАЛЬНО: писатель НЕ встаёт на паузу.
                        // Если бы пауза блокировала и писателя, канал
                        // переполнялся бы, читатель вставал бы на
                        // WriteAsync — и копирование зависало бы насмерть.
                        // Пауза реализована на стороне читателя, то есть
                        // в буфер уже пописано не более 2 × bufferSize.
                        long writeStart = Stopwatch.GetTimestamp();
                        await dstStream.WriteAsync(block.Buffer.Memory.Slice(0, block.Count), ct);
                        currentWriteLatency = (Stopwatch.GetTimestamp() - writeStart) * 1000.0 / Stopwatch.Frequency;

                        totalCopied += block.Count;
                        bytesSinceReport += block.Count;

                        long now = Stopwatch.GetTimestamp();
                        double intervalSec = (now - lastReportTime) / (double)Stopwatch.Frequency;
                        if (intervalSec >= 0.1)
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
            }
            catch
            {
                // Ошибка писателя должна разблокировать читателя.
                channel.Writer.TryComplete(new IOException("Запись не удалась"));
                throw;
            }

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
    }
}
