using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
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

    public class DownloadSegment : INotifyPropertyChanged
    {
        private long _bytesDownloaded;
        private SegmentStatus _status;

        [PrimaryKey]
        public string Id { get; set; } = Guid.NewGuid().ToString();
        
        [Indexed]
        public string DownloadItemId { get; set; }
        public int Index { get; set; }
        public long StartPosition { get; set; }
        public long EndPosition { get; set; }
        
        public long BytesDownloaded 
        { 
            get => _bytesDownloaded; 
            set 
            { 
                _bytesDownloaded = value; 
                OnPropertyChanged();
                OnPropertyChanged(nameof(Progress));
            } 
        }
        
        public SegmentStatus Status 
        { 
            get => _status; 
            set { _status = value; OnPropertyChanged(); } 
        }

        [Ignore]
        public double Progress 
        {
            get
            {
                long totalBytes = EndPosition - StartPosition + 1;
                if (totalBytes == 0) return 0;
                return (double)_bytesDownloaded / totalBytes * 100.0;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
