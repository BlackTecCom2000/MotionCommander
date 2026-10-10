using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Win11CopyDialog.Modules.Utilities.DownloadManager.Models;

namespace Win11CopyDialog.Modules.Utilities.DownloadManager.Services
{
    public class SegmentManager
    {
        private static readonly HttpClient _sharedClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        
        private readonly DatabaseService _dbService;
        private readonly DownloadItem _downloadItem;
        private readonly bool _supportsRanges;

        public SegmentManager(DatabaseService dbService, DownloadItem downloadItem, bool supportsRanges = true)
        {
            _dbService = dbService;
            _downloadItem = downloadItem;
            _supportsRanges = supportsRanges;
        }

        /// <summary>
        /// Создаёт сегменты загрузки с учётом поддержки Range и минимального размера части.
        /// </summary>
        public async Task InitializeSegmentsAsync(int numberOfSegments)
        {
            if (_downloadItem.Segments.Any())
                return;

            if (_supportsRanges && _downloadItem.TotalBytes > 0)
            {
                if (numberOfSegments < 1) numberOfSegments = 1;
                // Не дробим части меньше 256 КБ — накладные расходы на HTTP-запросы превысят выгоду
                int maxSensibleSegments = (int)Math.Max(1, _downloadItem.TotalBytes / (256 * 1024));
                numberOfSegments = Math.Clamp(numberOfSegments, 1, Math.Min(16, maxSensibleSegments));

                long segmentSize = _downloadItem.TotalBytes / numberOfSegments;
                if (segmentSize <= 0) segmentSize = _downloadItem.TotalBytes;

                for (int i = 0; i < numberOfSegments; i++)
                {
                    var segment = new DownloadSegment
                    {
                        DownloadItemId = _downloadItem.Id,
                        OwnerDownloadItem = _downloadItem,
                        Index = i,
                        StartPosition = i * segmentSize,
                        EndPosition = (i == numberOfSegments - 1) ? _downloadItem.TotalBytes - 1 : (i + 1) * segmentSize - 1,
                        Status = SegmentStatus.Pending,
                        BytesDownloaded = 0
                    };

                    await AddSegmentToUiAsync(segment).ConfigureAwait(false);
                    await _dbService.SaveSegmentAsync(segment).ConfigureAwait(false);
                }
            }
            else
            {
                // Сервер без поддержки Range или с неизвестным размером: ровно один сегмент
                var segment = new DownloadSegment
                {
                    DownloadItemId = _downloadItem.Id,
                    OwnerDownloadItem = _downloadItem,
                    Index = 0,
                    StartPosition = 0,
                    EndPosition = _downloadItem.TotalBytes > 0 ? _downloadItem.TotalBytes - 1 : -1,
                    Status = SegmentStatus.Pending,
                    BytesDownloaded = 0
                };

                await AddSegmentToUiAsync(segment).ConfigureAwait(false);
                await _dbService.SaveSegmentAsync(segment).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Добавляет сегмент в коллекцию интерфейса без взаимной блокировки потока.
        /// </summary>
        private static Task AddSegmentToUiAsync(DownloadSegment segment)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                segment.OwnerDownloadItem?.Segments.Add(segment);
                return Task.CompletedTask;
            }

            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
                return Task.CompletedTask;

            if (dispatcher.CheckAccess())
            {
                segment.OwnerDownloadItem?.Segments.Add(segment);
                return Task.CompletedTask;
            }

            return dispatcher.InvokeAsync(() => segment.OwnerDownloadItem?.Segments.Add(segment))
                             .Task;
        }

        /// <summary>
        /// Скачивает отдельный сегмент с поддержкой возобновления, автоматических повторов и проверки на диске.
        /// </summary>
        public async Task DownloadSegmentAsync(DownloadSegment segment, CancellationToken cancellationToken)
        {
            segment.Status = SegmentStatus.Downloading;
            await _dbService.SaveSegmentAsync(segment).ConfigureAwait(false);

            var segmentFilePath = $"{_downloadItem.SavePath}.part{segment.Index}";
            string? targetDirectory = Path.GetDirectoryName(segmentFilePath);
            if (!string.IsNullOrEmpty(targetDirectory) && !Directory.Exists(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            // 1. Проверяем состояние существующего файла части на диске
            long expectedSegmentLength = (segment.EndPosition >= segment.StartPosition)
                ? segment.EndPosition - segment.StartPosition + 1
                : -1;

            if (_supportsRanges)
            {
                if (File.Exists(segmentFilePath))
                {
                    long diskLength = new FileInfo(segmentFilePath).Length;
                    if (expectedSegmentLength > 0 && diskLength >= expectedSegmentLength)
                    {
                        // Сегмент уже полностью скачан на диске!
                        segment.BytesDownloaded = expectedSegmentLength;
                        segment.Status = SegmentStatus.Completed;
                        await _dbService.SaveSegmentAsync(segment).ConfigureAwait(false);
                        return;
                    }

                    if (expectedSegmentLength > 0 && diskLength > 0 && diskLength < expectedSegmentLength)
                    {
                        // Подтверждённый размер на диске — продолжаем именно с него
                        segment.BytesDownloaded = diskLength;
                    }
                    else if (expectedSegmentLength <= 0 && diskLength > 0)
                    {
                        segment.BytesDownloaded = diskLength;
                    }
                }
                else
                {
                    segment.BytesDownloaded = 0;
                }
            }
            else
            {
                // Сервер без поддержки Range не поддерживает докачку с середины.
                // При возобновлении файл перекачивается заново с нуля.
                if (segment.BytesDownloaded > 0)
                {
                    segment.BytesDownloaded = 0;
                    _downloadItem.BytesDownloaded = 0;
                    await _dbService.SaveSegmentAsync(segment).ConfigureAwait(false);
                }
            }

            // 2. Цикл скачивания с поддержкой повторных попыток при сетевых сбоях
            int retryCount = 0;
            const int maxRetries = 3;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, _downloadItem.Url);
                    long currentStart = segment.StartPosition + segment.BytesDownloaded;

                    if (_supportsRanges && segment.EndPosition >= currentStart)
                    {
                        request.Headers.Range = new RangeHeaderValue(currentStart, segment.EndPosition);
                    }

                    using var response = await _sharedClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();

                    if (_supportsRanges && segment.EndPosition >= 0)
                    {
                        var range = response.Content.Headers.ContentRange;
                        if (response.StatusCode == System.Net.HttpStatusCode.PartialContent)
                        {
                            if (range?.From != currentStart)
                            {
                                throw new InvalidDataException($"Сервер вернул неверное смещение диапазона: получено {range?.From}, ожидалось {currentStart}.");
                            }
                        }
                        else if (response.StatusCode == System.Net.HttpStatusCode.OK)
                        {
                            // Сервер проигнорировал заголовок Range и отдал весь файл целиком
                            if (currentStart > 0)
                            {
                                throw new InvalidDataException("Сервер проигнорировал докачку (Range) и вернул файл с начала. Перезапуск части.");
                            }
                            segment.BytesDownloaded = 0;
                        }
                    }

                    using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

                    // Режим открытия: если докачиваем — Open и выравнивание размера; с нуля — Create
                    FileMode mode = segment.BytesDownloaded > 0 ? FileMode.Open : FileMode.Create;
                    using var fileStream = new FileStream(segmentFilePath, mode, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous);

                    if (segment.BytesDownloaded > 0)
                    {
                        // Обрезаем возможные остаточные хвосты от сбоев
                        fileStream.SetLength(segment.BytesDownloaded);
                        fileStream.Seek(segment.BytesDownloaded, SeekOrigin.Begin);
                    }

                    byte[] buffer = ArrayPool<byte>.Shared.Rent(131072);
                    try
                    {
                        int bytesRead;
                        while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                        {
                            await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);
                            segment.BytesDownloaded += bytesRead;
                            _downloadItem.AddBytesDownloaded(bytesRead);
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }

                    await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);

                    // Если размер был неизвестен (chunked/stream), фиксируем фактический размер
                    if (segment.EndPosition < 0)
                    {
                        segment.EndPosition = segment.BytesDownloaded - 1;
                        _downloadItem.TotalBytes = segment.BytesDownloaded;
                    }

                    // Проверяем размер скачанной части
                    long finalPartLength = fileStream.Length;
                    long expected = segment.EndPosition - segment.StartPosition + 1;
                    if (expected > 0 && finalPartLength != expected)
                    {
                        throw new InvalidDataException($"Часть {segment.Index + 1} загружена не полностью: на диске {finalPartLength} байт, ожидалось {expected} байт.");
                    }

                    segment.Status = SegmentStatus.Completed;
                    await _dbService.SaveSegmentAsync(segment).ConfigureAwait(false);
                    return; // Успешно завершено!
                }
                catch (OperationCanceledException)
                {
                    segment.Status = SegmentStatus.Pending;
                    await _dbService.SaveSegmentAsync(segment).ConfigureAwait(false);
                    throw;
                }
                catch (Exception) when (retryCount < maxRetries && !cancellationToken.IsCancellationRequested)
                {
                    retryCount++;
                    // Задержка перед повторной попыткой
                    await Task.Delay(1000 * retryCount, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    segment.Status = SegmentStatus.Failed;
                    await _dbService.SaveSegmentAsync(segment).ConfigureAwait(false);
                    throw;
                }
            }
        }

        /// <summary>
        /// Выполняет проверку целостности всех сегментов и собирает итоговый файл.
        /// </summary>
        public async Task MergeSegmentsAsync(CancellationToken cancellationToken = default)
        {
            var finalFilePath = _downloadItem.SavePath;
            var segments = _downloadItem.Segments.OrderBy(s => s.Index).ToList();

            if (segments.Count == 0)
            {
                throw new InvalidDataException("Список частей загрузки пуст. Итоговый файл не может быть собран.");
            }

            // 1. Проверка структуры сегментов на непрерывность и полноту
            if (segments[0].StartPosition != 0)
            {
                throw new InvalidDataException("Первая часть файла имеет неверное начальное смещение.");
            }

            for (int i = 1; i < segments.Count; i++)
            {
                if (segments[i].StartPosition != segments[i - 1].EndPosition + 1)
                {
                    throw new InvalidDataException($"Обнаружен разрыв или наложение между частями {i} и {i + 1}. Сборка отменена для защиты целостности данных.");
                }
            }

            if (_downloadItem.TotalBytes > 0 && segments[^1].EndPosition != _downloadItem.TotalBytes - 1)
            {
                throw new InvalidDataException("Конечная позиция последней части не соответствует общему размеру файла.");
            }

            // 2. Проверка физических файлов частей на диске
            long totalPhysicalBytes = 0;
            foreach (var segment in segments)
            {
                if (segment.Status != SegmentStatus.Completed)
                {
                    throw new InvalidDataException($"Часть {segment.Index + 1} не завершена (статус: {segment.Status}). Сборка невозможна.");
                }

                string segmentFilePath = $"{_downloadItem.SavePath}.part{segment.Index}";
                if (!File.Exists(segmentFilePath))
                {
                    throw new FileNotFoundException($"Файл части {segment.Index + 1} не найден на диске: {segmentFilePath}", segmentFilePath);
                }

                long expectedLength = segment.EndPosition - segment.StartPosition + 1;
                long actualLength = new FileInfo(segmentFilePath).Length;
                if (expectedLength > 0 && actualLength != expectedLength)
                {
                    throw new InvalidDataException($"Файл части {segment.Index + 1} повреждён: размер {actualLength} байт, ожидалось {expectedLength} байт.");
                }

                totalPhysicalBytes += actualLength;
            }

            if (_downloadItem.TotalBytes > 0 && totalPhysicalBytes != _downloadItem.TotalBytes)
            {
                throw new InvalidDataException($"Суммарный размер всех частей ({totalPhysicalBytes} байт) не совпадает с заявленным размером ({_downloadItem.TotalBytes} байт).");
            }

            // 3. Проверка свободного места на целевом накопителе
            CheckFreeDiskSpace(finalFilePath, segments.Count > 1 ? _downloadItem.TotalBytes : 0);

            string? directory = Path.GetDirectoryName(finalFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 4. Оптимизация для одного сегмента: прямое перемещение без двойного копирования
            if (segments.Count == 1)
            {
                string singlePartPath = $"{_downloadItem.SavePath}.part{segments[0].Index}";

                // Проверка контрольной суммы, если задана
                if (!string.IsNullOrWhiteSpace(_downloadItem.ExpectedHash))
                {
                    string computedHash = await ComputeFileHashAsync(singlePartPath, _downloadItem.HashAlgorithm, cancellationToken).ConfigureAwait(false);
                    _downloadItem.VerifiedHash = computedHash;
                    if (!computedHash.Equals(_downloadItem.ExpectedHash.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException($"Контрольная сумма файла не совпадает! Ожидалось: {_downloadItem.ExpectedHash}, вычислено: {computedHash}.");
                    }
                }

                File.Move(singlePartPath, finalFilePath, overwrite: true);
                return;
            }

            // 5. Многосегментная сборка с потоковым расчётом контрольной суммы
            string temporaryPath = finalFilePath + ".assembling";
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);

            IncrementalHash? hasher = CreateIncrementalHash(_downloadItem.HashAlgorithm, _downloadItem.ExpectedHash);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(131072);

            try
            {
                using (var finalStream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous))
                {
                    foreach (var segment in segments)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        string segmentFilePath = $"{_downloadItem.SavePath}.part{segment.Index}";
                        using (var segmentStream = new FileStream(segmentFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous))
                        {
                            int bytesRead;
                            while ((bytesRead = await segmentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                            {
                                await finalStream.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);
                                hasher?.AppendData(buffer, 0, bytesRead);
                            }
                        }
                    }

                    await finalStream.FlushAsync(cancellationToken).ConfigureAwait(false);

                    if (finalStream.Length != _downloadItem.TotalBytes)
                    {
                        throw new InvalidDataException($"Размер собранного файла ({finalStream.Length} байт) не совпадает с ожидаемым ({_downloadItem.TotalBytes} байт).");
                    }
                }

                // 6. Проверка контрольной суммы
                if (hasher != null)
                {
                    string actualHash = Convert.ToHexString(hasher.GetHashAndReset());
                    _downloadItem.VerifiedHash = actualHash;
                    if (!actualHash.Equals(_downloadItem.ExpectedHash?.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException($"Контрольная сумма собранного файла не совпадает! Ожидалось: {_downloadItem.ExpectedHash}, вычислено: {actualHash}. Файл отклонён в целях безопасности.");
                    }
                }

                // 7. Атомарная замена и очистка частей
                File.Move(temporaryPath, finalFilePath, overwrite: true);

                foreach (var segment in segments)
                {
                    string segmentFilePath = $"{_downloadItem.SavePath}.part{segment.Index}";
                    if (File.Exists(segmentFilePath))
                    {
                        try { File.Delete(segmentFilePath); } catch { }
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                hasher?.Dispose();

                // При сбое гарантированно удаляем незавершённый временный файл сборки
                if (File.Exists(temporaryPath))
                {
                    try { File.Delete(temporaryPath); } catch { }
                }
            }
        }

        private static void CheckFreeDiskSpace(string targetPath, long requiredBytes)
        {
            if (requiredBytes <= 0) return;
            try
            {
                string? root = Path.GetPathRoot(Path.GetFullPath(targetPath));
                if (!string.IsNullOrEmpty(root))
                {
                    var drive = new DriveInfo(root);
                    if (drive.IsReady && drive.AvailableFreeSpace < requiredBytes)
                    {
                        throw new IOException($"Недостаточно свободного места на накопителе {drive.Name} для сборки файла. Требуется: {FormatSize(requiredBytes)}, доступно: {FormatSize(drive.AvailableFreeSpace)}.");
                    }
                }
            }
            catch (IOException) { throw; }
            catch { /* Опрос свободного места может быть недоступен на специальных сетевых путях */ }
        }

        private static string FormatSize(long bytes)
        {
            if (bytes >= 1024L * 1024L * 1024L)
                return $"{(double)bytes / (1024L * 1024L * 1024L):0.##} GB";
            if (bytes >= 1024L * 1024L)
                return $"{(double)bytes / (1024L * 1024L):0.##} MB";
            if (bytes >= 1024L)
                return $"{(double)bytes / 1024L:0.##} KB";
            return $"{bytes} B";
        }

        private static IncrementalHash? CreateIncrementalHash(string? algorithm, string? expectedHash)
        {
            if (string.IsNullOrWhiteSpace(expectedHash)) return null;

            HashAlgorithmName algoName = (algorithm?.ToUpperInvariant()) switch
            {
                "MD5" => HashAlgorithmName.MD5,
                "SHA1" => HashAlgorithmName.SHA1,
                "SHA384" => HashAlgorithmName.SHA384,
                "SHA512" => HashAlgorithmName.SHA512,
                _ => HashAlgorithmName.SHA256
            };
            return IncrementalHash.CreateHash(algoName);
        }

        private static async Task<string> ComputeFileHashAsync(string filePath, string? algorithm, CancellationToken cancellationToken)
        {
            HashAlgorithmName algoName = (algorithm?.ToUpperInvariant()) switch
            {
                "MD5" => HashAlgorithmName.MD5,
                "SHA1" => HashAlgorithmName.SHA1,
                "SHA384" => HashAlgorithmName.SHA384,
                "SHA512" => HashAlgorithmName.SHA512,
                _ => HashAlgorithmName.SHA256
            };

            using var hasher = IncrementalHash.CreateHash(algoName);
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(131072);
            try
            {
                int read;
                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hasher.AppendData(buffer, 0, read);
                }
                return Convert.ToHexString(hasher.GetHashAndReset());
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
