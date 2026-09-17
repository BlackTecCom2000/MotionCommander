using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using SQLite;

namespace Win11CopyDialog.Modules.Utilities.DownloadManager.Models
{
    public enum DownloadStatus
    {
        Queued,
        Downloading,
        Paused,
        Completed,
        Failed,
        Verifying
    }

    public class DownloadItem : INotifyPropertyChanged
    {
        private long _bytesDownloaded;
        private DownloadStatus _status;

        [PrimaryKey]
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Url { get; set; }
        public string FileName { get; set; }
        public string SavePath { get; set; }
        public long TotalBytes { get; set; }

        public long BytesDownloaded
        {
            get => _bytesDownloaded;
            set { _bytesDownloaded = value; OnPropertyChanged(); }
        }

        public DownloadStatus Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(); }
        }

        public DateTime DateAdded { get; set; } = DateTime.Now;
        public DateTime? DateCompleted { get; set; }
        public string ErrorMessage { get; set; }
        
        // Navigation property
        [Ignore]
        public List<DownloadSegment> Segments { get; set; } = new List<DownloadSegment>();

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
