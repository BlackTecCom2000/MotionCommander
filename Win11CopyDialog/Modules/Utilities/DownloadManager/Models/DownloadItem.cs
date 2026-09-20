using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
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
        private double _speed;
        private ObservableCollection<DownloadSegment> _segments = new ObservableCollection<DownloadSegment>();

        [PrimaryKey]
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Url { get; set; }
        public string FileName { get; set; }
        public string SavePath { get; set; }
        public long TotalBytes { get; set; }

        public long BytesDownloaded
        {
            get => Interlocked.Read(ref _bytesDownloaded);
            set 
            { 
                Interlocked.Exchange(ref _bytesDownloaded, value); 
                OnPropertyChanged(); 
                OnPropertyChanged(nameof(Progress)); 
            }
        }
        
        public void AddBytesDownloaded(long bytes)
        {
            Interlocked.Add(ref _bytesDownloaded, bytes);
            // Notice: we do not call OnPropertyChanged here to avoid UI thread flooding.
            // The DownloadEngine will trigger OnPropertyChanged periodically.
        }
        
        [Ignore]
        public double Speed
        {
            get => _speed;
            set { _speed = value; OnPropertyChanged(); OnPropertyChanged(nameof(SpeedText)); }
        }

        [Ignore]
        public string SpeedText
        {
            get
            {
                if (_speed > 1024 * 1024)
                    return $"{(_speed / 1024 / 1024):0.##} MB/s";
                if (_speed > 1024)
                    return $"{(_speed / 1024):0.##} KB/s";
                return $"{_speed:0} B/s";
            }
        }

        [Ignore]
        public double Progress
        {
            get
            {
                if (TotalBytes == 0) return 0;
                return (double)_bytesDownloaded / TotalBytes * 100.0;
            }
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
        public ObservableCollection<DownloadSegment> Segments 
        { 
            get => _segments;
            set { _segments = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
