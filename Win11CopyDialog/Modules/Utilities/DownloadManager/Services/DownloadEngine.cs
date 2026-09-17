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
        private CancellationTokenSource _cancellationTokenSource;

        public event EventHandler<DownloadItem> ProgressChanged;
        public event EventHandler<DownloadItem> DownloadCompleted;
        public event EventHandler<DownloadItem> DownloadFailed;

        public DownloadEngine(DatabaseService dbService, DownloadTaskConfig config)
        {
            _dbService = dbService;
            _config = config;
        }

        public async Task StartDownloadAsync(DownloadItem item)
        {
            _cancellationTokenSource = new CancellationTokenSource();
            item.Status = DownloadStatus.Downloading;
            item.ErrorMessage = null;
            await _dbService.SaveDownloadAsync(item);

            try
            {
                // Ensure total size is known
                if (item.TotalBytes == 0)
                {
                    item.TotalBytes = await GetFileSizeAsync(item.Url);
                    await _dbService.SaveDownloadAsync(item);
                }

                var segmentManager = new SegmentManager(_dbService, item);
                
                // Initialize segments if not already done
                if (!item.Segments.Any())
                {
                    await segmentManager.InitializeSegmentsAsync(_config.MaxConcurrentSegments);
                }

                var tasks = new List<Task>();
                foreach (var segment in item.Segments.Where(s => s.Status != SegmentStatus.Completed))
                {
                    tasks.Add(segmentManager.DownloadSegmentAsync(segment, _cancellationTokenSource.Token));
                }

                // Periodic progress saving could be a background loop here
                var progressTask = Task.Run(async () =>
                {
                    while (!_cancellationTokenSource.IsCancellationRequested && item.Status == DownloadStatus.Downloading)
                    {
                        await Task.Delay(1000);
                        ProgressChanged?.Invoke(this, item);
                        await _dbService.SaveDownloadAsync(item);
                    }
                });

                await Task.WhenAll(tasks);

                item.Status = DownloadStatus.Verifying;
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
                await _dbService.SaveDownloadAsync(item);
            }
            catch (Exception ex)
            {
                item.Status = DownloadStatus.Failed;
                item.ErrorMessage = ex.Message;
                await _dbService.SaveDownloadAsync(item);
                DownloadFailed?.Invoke(this, item);
            }
            finally
            {
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }
        }

        public async Task PauseDownloadAsync()
        {
            if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
            {
                _cancellationTokenSource.Cancel();
            }
            await Task.CompletedTask;
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
