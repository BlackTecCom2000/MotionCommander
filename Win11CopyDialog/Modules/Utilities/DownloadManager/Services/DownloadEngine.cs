using System;
using System.Collections.Generic;
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

        // Поле nullable: в блоке finally ему присваивается null (строка 120).
        private CancellationTokenSource? _cancellationTokenSource;

        // События nullable: без инициализатора компилятор требовал
        // непустое значение на выходе из конструктора. Возбуждаются
        // только после реальной подписки, поэтому "нет подписчиков" —
        // штатное состояние.
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

            // Локальная ссылка на токен. Раньше фоновые циклы обращались к
            // полю _cancellationTokenSource, которое finally обнулял ДО их
            // завершения. Разбуженный через Task.Delay цикл обращался к null
            // и ронял NullReferenceException ВНУТРИ неотслеживаемой задачи,
            // то есть исключение просто исчезало. Это происходило на каждом
            // успешно завершённом скачивании.
            var token = cts.Token;

            item.Status = DownloadStatus.Downloading;
            item.ErrorMessage = null;  // ErrorMessage теперь string?
            await _dbService.SaveDownloadAsync(item);

            try
            {
                if (item.TotalBytes == 0)
                {
                    item.TotalBytes = await GetFileSizeAsync(item.Url);
                    await _dbService.SaveDownloadAsync(item);
                }

                var segmentManager = new SegmentManager(_dbService, item);
                
                if (!item.Segments.Any())
                {
                    await segmentManager.InitializeSegmentsAsync(_config.MaxConcurrentSegments);
                }

                var tasks = new List<Task>();
                foreach (var segment in item.Segments.Where(s => s.Status != SegmentStatus.Completed))
                {
                    tasks.Add(segmentManager.DownloadSegmentAsync(segment, token));
                }

                // High-performance smooth UI Update Throttler (approx 30fps update rate)
                var progressTask = Task.Run(async () =>
                {
                    long lastBytes = item.BytesDownloaded;
                    while (!token.IsCancellationRequested && item.Status == DownloadStatus.Downloading)
                    {
                        await Task.Delay(33, token).ConfigureAwait(false);

                        long currentBytes = item.BytesDownloaded;
                        long delta = currentBytes - lastBytes;
                        lastBytes = currentBytes;

                        // Calculate speed per second (delta is over 33ms, so multiply by 30)
                        item.Speed = delta * (1000.0 / 33.0);

                        // Trigger UI updates safely
                        item.OnPropertyChanged(nameof(item.BytesDownloaded));
                        item.OnPropertyChanged(nameof(item.Progress));

                        ProgressChanged?.Invoke(this, item);
                    }
                }, token);

                // Periodically save state to DB
                var dbSaveTask = Task.Run(async () =>
                {
                    while (!token.IsCancellationRequested && item.Status == DownloadStatus.Downloading)
                    {
                        await Task.Delay(5000, token).ConfigureAwait(false);
                        await _dbService.SaveDownloadAsync(item).ConfigureAwait(false);
                    }
                }, token);

                try
                {
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
                finally
                {
                    // Фоновые циклы останавливаются ДО выхода из метода,
                    // иначе они продолжают работать с уже завершённой загрузкой.
                    cts.Cancel();
                    try { await Task.WhenAll(progressTask, dbSaveTask).ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    catch { }
                }

                item.Status = DownloadStatus.Verifying;
                item.Speed = 0;
                item.OnPropertyChanged(nameof(item.Status));
                ProgressChanged?.Invoke(this, item);

                await segmentManager.MergeSegmentsAsync();

                item.Status = DownloadStatus.Completed;
                item.DateCompleted = DateTime.Now;
                await _dbService.SaveDownloadAsync(item);
                
                DownloadCompleted?.Invoke(this, item);
            }
            catch (OperationCanceledException)
            {
                item.Status = DownloadStatus.Paused;
                item.Speed = 0;
                await _dbService.SaveDownloadAsync(item);
            }
            catch (Exception ex)
            {
                item.Status = DownloadStatus.Failed;
                item.ErrorMessage = ex.Message;
                item.Speed = 0;
                await _dbService.SaveDownloadAsync(item);
                DownloadFailed?.Invoke(this, item);
            }
            finally
            {
                // Поле обнуляется только здесь, и только если оно всё ещё
                // указывает на НАШ источник отмены. Иначе гонка: если
                // PauseDownloadAsync успел заменить поле, старая ссылка
                // освобождала бы уже используемый CTS.
                cts.Cancel();
                Interlocked.CompareExchange(ref _cancellationTokenSource, null, cts);
                cts.Dispose();
            }
        }

        /// <summary>
        /// Приостанавливает передачу.
        ///
        /// <para>Раньше здесь был ObjectDisposedException, когда вызов приходил
        /// после завершения: поле к этому моменту обнулялось, а если нет —
        /// CTS уже освобождался в finally. Теперь ссылка берётся атомарно,
        /// и отменяется только живой CTS.</para>
        /// </summary>
        public Task PauseDownloadAsync()
        {
            var cts = Volatile.Read(ref _cancellationTokenSource);
            if (cts == null) return Task.CompletedTask;
            try
            {
                if (!cts.IsCancellationRequested) cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Передача уже завершилась — ничего отменять не нужно.
            }
            return Task.CompletedTask;
        }

        private async Task<long> GetFileSizeAsync(string url)
        {
            using var client = new HttpClient();
            var request = new HttpRequestMessage(HttpMethod.Head, url);
            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return response.Content.Headers.ContentLength ?? 0;
        }
    }
}
