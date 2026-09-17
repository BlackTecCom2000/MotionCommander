using System;
using SQLite;

namespace Win11CopyDialog.Modules.Utilities.DownloadManager.Models
{
    public enum SegmentStatus
    {
        Pending,
        Downloading,
        Completed,
        Failed
    }

    public class DownloadSegment
    {
        [PrimaryKey]
        public string Id { get; set; } = Guid.NewGuid().ToString();
        
        [Indexed]
        public string DownloadItemId { get; set; }
        public int Index { get; set; }
        public long StartPosition { get; set; }
        public long EndPosition { get; set; }
        public long BytesDownloaded { get; set; }
        public SegmentStatus Status { get; set; }
    }
}
