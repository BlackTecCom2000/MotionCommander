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
        public string Url { get; set; } = "";
        public string FileName { get; set; } = "";
        public string SavePath { get; set; } = "";
        public long TotalBytes { get; set; }

        public long BytesDownloaded
        {
            get => Interlocked.Read(ref _bytesDownloaded);
            set 
            { 
                Interlocked.Exchange(ref _bytesDownloaded, value); 
                OnPropertyChanged(); 
                OnPropertyChanged(nameof(Progress)); 
                OnPropertyChanged(nameof(ProgressText));
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
                if (TotalBytes <= 0) return 0;
                return Math.Clamp((double)BytesDownloaded / TotalBytes * 100.0, 0.0, 100.0);
            }
        }

        public DownloadStatus Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(); }
        }

        public DateTime DateAdded { get; set; } = DateTime.Now;
        public DateTime? DateCompleted { get; set; }
        public string? ErrorMessage { get; set; }
        
        /// <summary>Ожидаемая контрольная сумма (SHA256, MD5 и т.д.) для проверки целостности перед сборкой.</summary>
        public string? ExpectedHash { get; set; }

        /// <summary>Алгоритм контрольной суммы (по умолчанию SHA256).</summary>
        public string HashAlgorithm { get; set; } = "SHA256";

        /// <summary>Фактическая контрольная сумма, вычисленная при сборке файла.</summary>
        public string? VerifiedHash { get; set; }

        /// <summary>Поддерживает ли удалённый сервер докачку по диапазонам байт (HTTP Range).</summary>
        public bool SupportsRanges { get; set; } = true;

        [Ignore]
        public string ProgressText
        {
            get
            {
                if (TotalBytes > 0)
                {
                    return $"{FormatBytes(BytesDownloaded)} / {FormatBytes(TotalBytes)} ({Progress:0.0}%)";
                }
                return FormatBytes(BytesDownloaded);
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1024L * 1024L * 1024L)
                return $"{(double)bytes / (1024L * 1024L * 1024L):0.##} GB";
            if (bytes >= 1024L * 1024L)
                return $"{(double)bytes / (1024L * 1024L):0.##} MB";
            if (bytes >= 1024L)
                return $"{(double)bytes / 1024L:0.##} KB";
            return $"{bytes} B";
        }
        
        // Navigation property
        [Ignore]
        public ObservableCollection<DownloadSegment> Segments 
        { 
            get => _segments;
            set { _segments = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
