using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Win11CopyDialog.Modules.Utilities.DownloadManager.Models;

namespace Win11CopyDialog.Modules.Utilities.DownloadManager.Services
{
    public class DownloadEngine
    {
        private readonly DatabaseService _dbService;
        private readonly DownloadTaskConfig _config;

        private CancellationTokenSource? _cancellationTokenSource;

        public event EventHandler<DownloadItem>? ProgressChanged;
        public event EventHandler<DownloadItem>? DownloadCompleted;
        public event EventHandler<DownloadItem>? DownloadFailed;

        public DownloadEngine(DatabaseService dbService, DownloadTaskConfig config)
        {
            _dbService = dbService;
            _config = config;
        }

        public async Task StartDownloadAsync(DownloadItem item)
        {
            var cts = new CancellationTokenSource();
            var previous = Interlocked.Exchange(ref _cancellationTokenSource, cts);
            previous?.Dispose();

            var token = cts.Token;

            item.Status = DownloadStatus.Downloading;
            item.ErrorMessage = null;
            await _dbService.SaveDownloadAsync(item).ConfigureAwait(false);

            try
            {
                var remoteInfo = await ProbeRemoteFileAsync(item.Url, token).ConfigureAwait(false);
                item.SupportsRanges = remoteInfo.SupportsRanges;

                if (string.IsNullOrWhiteSpace(item.ExpectedHash) && !string.IsNullOrWhiteSpace(remoteInfo.SuggestedHash))
                {
                    item.ExpectedHash = remoteInfo.SuggestedHash;
                }

                // 1. Проверяем, изменился ли файл на сервере
                bool lengthChanged = item.TotalBytes > 0 && remoteInfo.Length > 0 && item.TotalBytes != remoteInfo.Length;
                bool rangeSupportLost = !remoteInfo.SupportsRanges && item.Segments.Count > 1;

                if (lengthChanged || rangeSupportLost)
                {
                    // Файл на сервере изменился или сервер не поддерживает старую сегментацию
                    foreach (var segment in item.Segments)
                    {
                        string stalePart = $"{item.SavePath}.part{segment.Index}";
                        if (File.Exists(stalePart))
                        {
                            try { File.Delete(stalePart); } catch { }
                        }
                    }
                    item.Segments.Clear();
                    item.BytesDownloaded = 0;
                }

                if (remoteInfo.Length >= 0)
                {
                    item.TotalBytes = remoteInfo.Length;
                }
                await _dbService.SaveDownloadAsync(item).ConfigureAwait(false);

                // Если файл пустой (0 байт) — мгновенное завершение
                if (remoteInfo.Length == 0)
                {
                    string? targetDirectory = Path.GetDirectoryName(item.SavePath);
                    if (!string.IsNullOrEmpty(targetDirectory)) Directory.CreateDirectory(targetDirectory);
                    await File.WriteAllBytesAsync(item.SavePath, Array.Empty<byte>(), token).ConfigureAwait(false);
                    item.Status = DownloadStatus.Completed;
                    item.DateCompleted = DateTime.Now;
                    await _dbService.SaveDownloadAsync(item).ConfigureAwait(false);
                    DownloadCompleted?.Invoke(this, item);
                    return;
                }

                var segmentManager = new SegmentManager(_dbService, item, remoteInfo.SupportsRanges);
                
                // 2. Инициализация сегментов при первом запуске
                if (!item.Segments.Any())
                {
                    await segmentManager.InitializeSegmentsAsync(remoteInfo.SegmentCount(_config.MaxConcurrentSegments)).ConfigureAwait(false);
                }
                else
                {
                    // Синхронизируем уже существующие сегменты с реальными файлами на диске
                    foreach (var segment in item.Segments)
                    {
                        string partPath = $"{item.SavePath}.part{segment.Index}";
                        if (File.Exists(partPath))
                        {
                            long diskLen = new FileInfo(partPath).Length;
                            long expectedLen = segment.EndPosition - segment.StartPosition + 1;
                            if (expectedLen > 0 && diskLen >= expectedLen)
                            {
                                segment.BytesDownloaded = expectedLen;
                                segment.Status = SegmentStatus.Completed;
                            }
                            else if (expectedLen > 0 && diskLen > 0)
                            {
                                segment.BytesDownloaded = diskLen;
                                segment.Status = SegmentStatus.Pending;
                            }
                            else
                            {
                                segment.BytesDownloaded = 0;
                                segment.Status = SegmentStatus.Pending;
                            }
                        }
                        else
                        {
                            segment.BytesDownloaded = 0;
                            segment.Status = SegmentStatus.Pending;
                        }
                    }
                    item.BytesDownloaded = item.Segments.Sum(s => s.BytesDownloaded);
                    await _dbService.SaveDownloadAsync(item).ConfigureAwait(false);
                }

                // 3. Запуск скачивания только для незавершённых сегментов
                var tasks = new List<Task>();
                foreach (var segment in item.Segments.Where(s => s.Status != SegmentStatus.Completed))
                {
                    tasks.Add(segmentManager.DownloadSegmentAsync(segment, token));
                }

                // Плавное обновление UI (интервал ~33 мс для плавных 30 fps)
                var progressTask = Task.Run(async () =>
                {
                    long lastBytes = item.BytesDownloaded;
                    while (!token.IsCancellationRequested && item.Status == DownloadStatus.Downloading)
                    {
                        await Task.Delay(33, token).ConfigureAwait(false);

                        long currentBytes = item.BytesDownloaded;
                        long delta = currentBytes - lastBytes;
                        lastBytes = currentBytes;

                        item.Speed = Math.Max(0, delta * (1000.0 / 33.0));

                        item.OnPropertyChanged(nameof(item.BytesDownloaded));
                        item.OnPropertyChanged(nameof(item.Progress));
                        item.OnPropertyChanged(nameof(item.ProgressText));

                        ProgressChanged?.Invoke(this, item);
                    }
                }, token);

                // Периодическое сохранение прогресса в БД раз в 3 секунды
                var dbSaveTask = Task.Run(async () =>
                {
                    while (!token.IsCancellationRequested && item.Status == DownloadStatus.Downloading)
                    {
                        await Task.Delay(3000, token).ConfigureAwait(false);
                        await _dbService.SaveDownloadAsync(item).ConfigureAwait(false);
                    }
                }, token);

                try
                {
                    if (tasks.Count > 0)
                    {
                        await Task.WhenAll(tasks).ConfigureAwait(false);
                    }
                }
                finally
                {
                    cts.Cancel();
                    try { await Task.WhenAll(progressTask, dbSaveTask).ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    catch { }
                }

                // 4. Сборка и проверка целостности файлов
                item.Status = DownloadStatus.Verifying;
                item.Speed = 0;
                item.OnPropertyChanged(nameof(item.Status));
                item.OnPropertyChanged(nameof(item.Progress));
                ProgressChanged?.Invoke(this, item);

                // Для сборки создаём новый CTS с таймаутом на случай непредвиденных зависаний
                using var mergeCts = new CancellationTokenSource();
                await segmentManager.MergeSegmentsAsync(mergeCts.Token).ConfigureAwait(false);

                item.Status = DownloadStatus.Completed;
                item.DateCompleted = DateTime.Now;
                item.BytesDownloaded = item.TotalBytes;
                await _dbService.SaveDownloadAsync(item).ConfigureAwait(false);
                
                DownloadCompleted?.Invoke(this, item);
            }
            catch (OperationCanceledException)
            {
                item.Status = DownloadStatus.Paused;
                item.Speed = 0;
                await _dbService.SaveDownloadAsync(item).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                item.Status = DownloadStatus.Failed;
                item.ErrorMessage = ex.Message;
                item.Speed = 0;
                await _dbService.SaveDownloadAsync(item).ConfigureAwait(false);
                DownloadFailed?.Invoke(this, item);
            }
            finally
            {
                cts.Cancel();
                Interlocked.CompareExchange(ref _cancellationTokenSource, null, cts);
                cts.Dispose();
            }
        }

        public Task PauseDownloadAsync()
        {
            var cts = Volatile.Read(ref _cancellationTokenSource);
            if (cts == null) return Task.CompletedTask;
            try
            {
                if (!cts.IsCancellationRequested) cts.Cancel();
            }
            catch (ObjectDisposedException) { }
            return Task.CompletedTask;
        }

        private static async Task<RemoteFileInfo> ProbeRemoteFileAsync(string url, CancellationToken cancellationToken)
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                using var emptyHead = new HttpRequestMessage(HttpMethod.Head, url);
                using var emptyResponse = await client.SendAsync(emptyHead, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (emptyResponse.IsSuccessStatusCode && emptyResponse.Content.Headers.ContentLength == 0)
                    return new RemoteFileInfo(0, false, null);
                response.EnsureSuccessStatusCode();
            }
            response.EnsureSuccessStatusCode();

            // Извлечение хеша или ETag при наличии
            string? suggestedHash = null;
            string? etag = response.Headers.ETag?.Tag?.Trim('"', ' ', 'W', '/');
            if (!string.IsNullOrEmpty(etag) && (etag.Length == 32 || etag.Length == 64))
            {
                suggestedHash = etag;
            }

            // Сервер подтвердил докачку диапазонами
            if (response.StatusCode == System.Net.HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.Length is long rangeLength)
            {
                return new RemoteFileInfo(rangeLength, true, suggestedHash);
            }

            // Сервер вернул 200 OK (без поддержки Range)
            long? knownLength = response.Content.Headers.ContentLength;
            if (knownLength is null or 0)
            {
                using var head = new HttpRequestMessage(HttpMethod.Head, url);
                using var headResponse = await client.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (headResponse.IsSuccessStatusCode) knownLength ??= headResponse.Content.Headers.ContentLength;
            }

            if (knownLength.HasValue && knownLength.Value >= 0)
            {
                return new RemoteFileInfo(knownLength.Value, false, suggestedHash);
            }

            // Потоковая передача с неизвестным размером (chunked)
            return new RemoteFileInfo(-1, false, suggestedHash);
        }

        private sealed record RemoteFileInfo(long Length, bool SupportsRanges, string? SuggestedHash)
        {
            public int SegmentCount(int requested) => !SupportsRanges || Length <= 0 ? 1 :
                (int)Math.Min(Math.Max(1, requested), Length);
        }
    }
}
