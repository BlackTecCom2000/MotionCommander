namespace Win11CopyDialog.Modules.Utilities.DownloadManager.Models
{
    public class DownloadTaskConfig
    {
        public int MaxConcurrentSegments { get; set; } = 8;
        public long MaxBandwidthBytesPerSecond { get; set; } = 0; // 0 means unlimited
        public bool RequireAuthentication { get; set; } = false;
        public string Username { get; set; }
        public string Password { get; set; }
        public int RetryCount { get; set; } = 3;
    }
}
