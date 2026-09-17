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
        
        public DatabaseService()
        {
            var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MotionCommander", "downloads.db");
            var directory = Path.GetDirectoryName(dbPath);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            
            _db = new SQLiteAsyncConnection(dbPath);
            InitializeAsync().Wait();
        }

        private async Task InitializeAsync()
        {
            await _db.CreateTableAsync<DownloadItem>();
            await _db.CreateTableAsync<DownloadSegment>();
        }

        public async Task<List<DownloadItem>> GetAllDownloadsAsync()
        {
            var items = await _db.Table<DownloadItem>().OrderByDescending(x => x.DateAdded).ToListAsync();
            foreach (var item in items)
            {
                item.Segments = await GetSegmentsAsync(item.Id);
            }
            return items;
        }

        public async Task<DownloadItem> GetDownloadAsync(string id)
        {
            var item = await _db.Table<DownloadItem>().FirstOrDefaultAsync(x => x.Id == id);
            if (item != null)
            {
                item.Segments = await GetSegmentsAsync(id);
            }
            return item;
        }

        public async Task SaveDownloadAsync(DownloadItem item)
        {
            if (await _db.Table<DownloadItem>().CountAsync(x => x.Id == item.Id) > 0)
            {
                await _db.UpdateAsync(item);
            }
            else
            {
                await _db.InsertAsync(item);
            }

            foreach (var segment in item.Segments)
            {
                await SaveSegmentAsync(segment);
            }
        }

        public async Task DeleteDownloadAsync(string id)
        {
            await _db.DeleteAsync<DownloadItem>(id);
            var segments = await GetSegmentsAsync(id);
            foreach (var segment in segments)
            {
                await _db.DeleteAsync<DownloadSegment>(segment.Id);
            }
        }

        public async Task<List<DownloadSegment>> GetSegmentsAsync(string downloadItemId)
        {
            return await _db.Table<DownloadSegment>().Where(s => s.DownloadItemId == downloadItemId).ToListAsync();
        }

        public async Task SaveSegmentAsync(DownloadSegment segment)
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
    }
}
