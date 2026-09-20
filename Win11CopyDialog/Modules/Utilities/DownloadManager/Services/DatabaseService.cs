using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SQLite;
using Win11CopyDialog.Modules.Utilities.DownloadManager.Models;

namespace Win11CopyDialog.Modules.Utilities.DownloadManager.Services
{
    public class DatabaseService
    {
        private readonly SQLiteAsyncConnection _db;
        private readonly Task _initTask;
        
        public DatabaseService()
        {
            var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MotionCommander", "downloads.db");
            var directory = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            
            _db = new SQLiteAsyncConnection(dbPath);
            _initTask = Task.Run(async () =>
            {
                try
                {
                    await _db.CreateTableAsync<DownloadItem>().ConfigureAwait(false);
                    await _db.CreateTableAsync<DownloadSegment>().ConfigureAwait(false);
                }
                catch { }
            });
        }

        private async Task EnsureInitializedAsync()
        {
            try
            {
                await _initTask.ConfigureAwait(false);
            }
            catch { }
        }

        public async Task<List<DownloadItem>> GetAllDownloadsAsync()
        {
            await EnsureInitializedAsync();
            try
            {
                var items = await _db.Table<DownloadItem>().OrderByDescending(x => x.DateAdded).ToListAsync();
                foreach (var item in items)
                {
                    item.Segments = new System.Collections.ObjectModel.ObservableCollection<DownloadSegment>(await GetSegmentsAsync(item.Id));
                }
                return items;
            }
            catch
            {
                return new List<DownloadItem>();
            }
        }

        public async Task<DownloadItem?> GetDownloadAsync(string id)
        {
            await EnsureInitializedAsync();
            try
            {
                var item = await _db.Table<DownloadItem>().FirstOrDefaultAsync(x => x.Id == id);
                if (item != null)
                {
                    item.Segments = new System.Collections.ObjectModel.ObservableCollection<DownloadSegment>(await GetSegmentsAsync(id));
                }
                return item;
            }
            catch
            {
                return null;
            }
        }

        public async Task SaveDownloadAsync(DownloadItem item)
        {
            await EnsureInitializedAsync();
            try
            {
                if (await _db.Table<DownloadItem>().CountAsync(x => x.Id == item.Id) > 0)
                {
                    await _db.UpdateAsync(item);
                }
                else
                {
                    await _db.InsertAsync(item);
                }

                if (item.Segments != null)
                {
                    foreach (var segment in item.Segments)
                    {
                        await SaveSegmentAsync(segment);
                    }
                }
            }
            catch { }
        }

        public async Task DeleteDownloadAsync(string id)
        {
            await EnsureInitializedAsync();
            try
            {
                await _db.DeleteAsync<DownloadItem>(id);
                var segments = await GetSegmentsAsync(id);
                foreach (var segment in segments)
                {
                    await _db.DeleteAsync<DownloadSegment>(segment.Id);
                }
            }
            catch { }
        }

        public async Task<List<DownloadSegment>> GetSegmentsAsync(string downloadItemId)
        {
            await EnsureInitializedAsync();
            try
            {
                return await _db.Table<DownloadSegment>().Where(s => s.DownloadItemId == downloadItemId).ToListAsync();
            }
            catch
            {
                return new List<DownloadSegment>();
            }
        }

        public async Task SaveSegmentAsync(DownloadSegment segment)
        {
            await EnsureInitializedAsync();
            try
            {
                if (await _db.Table<DownloadSegment>().CountAsync(x => x.Id == segment.Id) > 0)
                {
                    await _db.UpdateAsync(segment);
                }
                else
                {
                    await _db.InsertAsync(segment);
                }
            }
            catch { }
        }
    }
}
