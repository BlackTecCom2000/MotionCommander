using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
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

        public SegmentManager(DatabaseService dbService, DownloadItem downloadItem)
        {
            _dbService = dbService;
            _downloadItem = downloadItem;
        }

        /// <summary>
        /// Создаёт сегменты загрузки.
        ///
        /// <para>Раньше коллекция, привязанная к интерфейсу, изменялась через
        /// App.Current.Dispatcher.Invoke. Это синхронная блокировка потока
        /// без проверки на завершение приложения: если окно уже закрывалось,
        /// Invoke ждал на диспетчере, который больше не обрабатывал очередь,
        /// и загрузка зависала навсегда. Кроме того, App.Current
        /// разыменовывался без проверки на null.</para>
        /// </summary>
        public async Task InitializeSegmentsAsync(int numberOfSegments)
        {
            if (_downloadItem.Segments.Any())
                return;

            if (numberOfSegments < 1) numberOfSegments = 1;
            long segmentSize = _downloadItem.TotalBytes / numberOfSegments;
            if (segmentSize <= 0) segmentSize = Math.Max(1, _downloadItem.TotalBytes);

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

        /// <summary>
        /// Добавляет сегмент в коллекцию, привязанную к интерфейсу.
        /// Безопасная альтернатива Dispatcher.Invoke: при завершении
        /// приложения операция просто пропускается, а не блокирует поток.
        /// </summary>
        private static Task AddSegmentToUiAsync(DownloadSegment segment)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
                return Task.CompletedTask;

            if (dispatcher.CheckAccess())
            {
                segment.OwnerDownloadItem?.Segments.Add(segment);
                return Task.CompletedTask;
            }

            return dispatcher.InvokeAsync(() => segment.OwnerDownloadItem?.Segments.Add(segment))
                             .Task;
        }

        public async Task DownloadSegmentAsync(DownloadSegment segment, CancellationToken cancellationToken)
        {
            segment.Status = SegmentStatus.Downloading;
            await _dbService.SaveSegmentAsync(segment);

            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, _downloadItem.Url);
                long currentStart = segment.StartPosition + segment.BytesDownloaded;
                request.Headers.Range = new RangeHeaderValue(currentStart, segment.EndPosition);

                using var response = await _sharedClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync();
                
                var segmentFilePath = $"{_downloadItem.SavePath}.part{segment.Index}";

                // РЕЖИМ ОТКРЫТИЯ ВАЖЕН ДЛЯ ЦЕЛОСТНОСТИ ФАЙЛА.
                // Раньше стоял FileMode.OpenOrCreate: при продолжении
                // загрузки, если сегмент от прошлой попытки длиннее
                // текущего, в файле оставался мусорный хвост, который
                // попадал в итоговый файл после склейки сегментов.
                //  - начать с нуля  -> Create (файл обрезается до нуля)
                //  - продолжить     -> Open с переходом на нужную позицию
                using var fileStream = segment.BytesDownloaded > 0
                    ? new FileStream(segmentFilePath, FileMode.Open, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous)
                    : new FileStream(segmentFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous);

                fileStream.Seek(segment.BytesDownloaded, SeekOrigin.Begin);

                // Zero-allocation buffering
                byte[] buffer = ArrayPool<byte>.Shared.Rent(131072);
                try
                {
                    int bytesRead;
                    while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                        
                        segment.BytesDownloaded += bytesRead;
                        
                        // Atomically update total bytes downloaded for the main item
                        _downloadItem.AddBytesDownloaded(bytesRead);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                segment.Status = SegmentStatus.Completed;
                await _dbService.SaveSegmentAsync(segment);
            }
            catch (OperationCanceledException)
            {
                segment.Status = SegmentStatus.Pending;
                await _dbService.SaveSegmentAsync(segment);
            }
            catch (Exception)
            {
                segment.Status = SegmentStatus.Failed;
                await _dbService.SaveSegmentAsync(segment);
                throw;
            }
        }

        public async Task MergeSegmentsAsync()
        {
            var finalFilePath = _downloadItem.SavePath;
            
            if (File.Exists(finalFilePath))
                File.Delete(finalFilePath);

            using var finalStream = new FileStream(finalFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(131072);

            try
            {
                foreach (var segment in _downloadItem.Segments.OrderBy(s => s.Index))
                {
                    var segmentFilePath = $"{_downloadItem.SavePath}.part{segment.Index}";
                    if (File.Exists(segmentFilePath))
                    {
                        using (var segmentStream = new FileStream(segmentFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous))
                        {
                            int bytesRead;
                            while ((bytesRead = await segmentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                            {
                                await finalStream.WriteAsync(buffer, 0, bytesRead);
                            }
                        }
                        
                        File.Delete(segmentFilePath);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
