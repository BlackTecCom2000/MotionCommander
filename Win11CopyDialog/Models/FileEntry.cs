using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Win11CopyDialog.Models;

public enum FileEntryType
{
    Drive,
    Folder,
    File,
    VirtualFolder
}

public enum FileIconSize
{
    Small = 16,
    Medium = 32,
    Large = 48,
    ExtraLarge = 256
}

public sealed class FileEntry : INotifyPropertyChanged
{
    public string FullPath { get; }
    public string Name { get; }
    public FileEntryType Type { get; }
    public DateTime LastWriteTime { get; }
    public DateTime CreationTime { get; }
    public FileAttributes Attributes { get; }
    
    private long _sizeBytes = -1;
    public long SizeBytes
    {
        get => _sizeBytes;
        private set { _sizeBytes = value; OnPropertyChanged(); OnPropertyChanged(nameof(SizeText)); }
    }

    public string SizeText => Type == FileEntryType.Folder || Type == FileEntryType.Drive 
        ? "" 
        : Helpers.Formatters.Bytes(SizeBytes);

    public string Extension => Type == FileEntryType.File ? Path.GetExtension(Name).ToLowerInvariant() : "";
    
    private ImageSource? _icon;
    public ImageSource? Icon
    {
        get => _icon;
        private set { _icon = value; OnPropertyChanged(); }
    }

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnPropertyChanged(); }
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    private bool _isLoadingChildren;
    public bool IsLoadingChildren
    {
        get => _isLoadingChildren;
        set { _isLoadingChildren = value; OnPropertyChanged(); }
    }

    public ObservableCollection<FileEntry> Children { get; } = new();

    public FileEntry(string fullPath, FileEntryType type, DateTime lastWrite, DateTime creation, FileAttributes attrs)
    {
        FullPath = fullPath;
        Name = type == FileEntryType.Drive ? fullPath : Path.GetFileName(fullPath);
        Type = type;
        LastWriteTime = lastWrite;
        CreationTime = creation;
        Attributes = attrs;
    }

    public void SetSize(long size) => SizeBytes = size;
    public void SetIcon(ImageSource icon) => Icon = icon;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class FileSystemIcons
{
    private static readonly Dictionary<string, ImageSource> _cache = new();
    private static readonly object _lock = new();
    private static ImageSource? _folderIcon;
    private static ImageSource? _driveIcon;
    private static ImageSource? _fileIcon;

    public static ImageSource GetIcon(string path, FileEntryType type, FileIconSize size = FileIconSize.Medium)
    {
        var key = $"{path}|{type}|{size}";
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var cached)) return cached;
        }

        ImageSource icon;
        try
        {
            var shinfo = new SHFILEINFO();
            uint flags = SHGFI_ICON | SHGFI_USEFILEATTRIBUTES | 
                (size == FileIconSize.Small ? SHGFI_SMALLICON : 
                 size == FileIconSize.Large ? SHGFI_LARGEICON : 0);
            
            FileAttributes attrs = type == FileEntryType.Folder ? FileAttributes.Directory : FileAttributes.Normal;
            
            SHGetFileInfo(path, (uint)attrs, ref shinfo, (uint)Marshal.SizeOf(shinfo), flags);
            
            if (shinfo.hIcon != IntPtr.Zero)
            {
                icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(shinfo.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                icon.Freeze();
                DestroyIcon(shinfo.hIcon);
            }
            else
            {
                icon = GetDefaultIcon(type, size);
            }
        }
        catch
        {
            icon = GetDefaultIcon(type, size);
        }

        lock (_lock) _cache[key] = icon;
        return icon;
    }

    private static ImageSource GetDefaultIcon(FileEntryType type, FileIconSize size)
    {
        return type switch
        {
            FileEntryType.Drive => _driveIcon ??= CreateGeometryIcon("M4 6H20V18H4V6Z M4 4H20V20H4V4Z", Colors.Goldenrod),
            FileEntryType.Folder => _folderIcon ??= CreateGeometryIcon("M10 4H4C2.9 4 2 4.9 2 6V18C2 19.1 2.9 20 4 20H20C21.1 20 22 19.1 22 18V8L14 4H10Z", Colors.Gold),
            _ => _fileIcon ??= CreateGeometryIcon("M14 2H6C4.9 2 4 2.9 4 4V20C4 21.1 4.9 22 6 22H18C19.1 22 20 21.1 20 20V8L14 2Z", Colors.LightGray)
        };
    }

    private static ImageSource CreateGeometryIcon(string pathData, Color color)
    {
        var geometry = Geometry.Parse(pathData);
        var drawing = new GeometryDrawing(new SolidColorBrush(color), null, geometry);
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_SMALLICON = 0x1;
    private const uint SHGFI_LARGEICON = 0x0;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x10;

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}

public static class FileService
{
    public static async Task<ObservableCollection<FileEntry>> GetDrivesAsync()
    {
        var drives = new ObservableCollection<FileEntry>();
        await Task.Run(() =>
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady) continue;
                    var entry = new FileEntry(drive.Name, FileEntryType.Drive, 
                        DateTime.Now, DateTime.Now, FileAttributes.Directory);
                    entry.SetIcon(FileSystemIcons.GetIcon(drive.Name, FileEntryType.Drive));
                    drives.Add(entry);
                }
                catch { /* skip inaccessible drives */ }
            }
        });
        return drives;
    }

    public static async Task LoadChildrenAsync(FileEntry parent, CancellationToken ct = default)
    {
        if (parent.Type != FileEntryType.Folder && parent.Type != FileEntryType.Drive) return;
        if (parent.Children.Any()) return; // уже загружено
        
        parent.IsLoadingChildren = true;
        
        await Task.Run(() =>
        {
            try
            {
                var dirs = Directory.GetDirectories(parent.FullPath);
                var files = Directory.GetFiles(parent.FullPath);

                foreach (var dir in dirs)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var info = new DirectoryInfo(dir);
                        if ((info.Attributes & FileAttributes.Hidden) != 0) continue;
                        if ((info.Attributes & FileAttributes.System) != 0) continue;
                        
                        var child = new FileEntry(dir, FileEntryType.Folder, 
                            info.LastWriteTime, info.CreationTime, info.Attributes);
                        child.SetIcon(FileSystemIcons.GetIcon(dir, FileEntryType.Folder));
                        Application.Current.Dispatcher.Invoke(() => parent.Children.Add(child));
                    }
                    catch { /* skip inaccessible */ }
                }

                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var info = new FileInfo(file);
                        if ((info.Attributes & FileAttributes.Hidden) != 0) continue;
                        if ((info.Attributes & FileAttributes.System) != 0) continue;
                        
                        var child = new FileEntry(file, FileEntryType.File, 
                            info.LastWriteTime, info.CreationTime, info.Attributes);
                        child.SetSize(info.Length);
                        child.SetIcon(FileSystemIcons.GetIcon(file, FileEntryType.File));
                        Application.Current.Dispatcher.Invoke(() => parent.Children.Add(child));
                    }
                    catch { /* skip inaccessible */ }
                }
            }
            catch (OperationCanceledException) { }
            catch { /* permission denied etc */ }
            finally
            {
                Application.Current.Dispatcher.Invoke(() => parent.IsLoadingChildren = false);
            }
        }, ct);
    }

    public static async Task<long> GetDirectorySizeAsync(string path, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            long size = 0;
            try
            {
                foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    try { size += new FileInfo(file).Length; } catch { }
                }
            }
            catch (OperationCanceledException) { }
            catch { }
            return size;
        }, ct);
    }

    public static async Task CopyAsync(FileEntry source, string destDir, IProgress<CopyProgress> progress, CancellationToken ct = default)
    {
        if (source.Type == FileEntryType.File)
        {
            await CopyFileAsync(source.FullPath, Path.Combine(destDir, source.Name), progress, ct);
        }
        else if (source.Type == FileEntryType.Folder)
        {
            await CopyDirectoryAsync(source.FullPath, Path.Combine(destDir, source.Name), progress, ct);
        }
    }

    private static async Task CopyFileAsync(string src, string dest, IProgress<CopyProgress> progress, CancellationToken ct)
    {
        const int BufferSize = 1024 * 1024;
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        
        using var sourceStream = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, true);
        using var destStream = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, true);
        
        var buffer = new byte[BufferSize];
        long totalBytes = sourceStream.Length;
        long copiedBytes = 0;
        int bytesRead;
        
        while ((bytesRead = await sourceStream.ReadAsync(buffer, ct)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            await destStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            copiedBytes += bytesRead;
            progress.Report(new CopyProgress 
            { 
                CurrentFile = Path.GetFileName(src),
                TotalBytes = totalBytes,
                CopiedBytes = copiedBytes,
                IsComplete = false
            });
        }
        
        progress.Report(new CopyProgress 
        { 
            CurrentFile = Path.GetFileName(src),
            TotalBytes = totalBytes,
            CopiedBytes = totalBytes,
            IsComplete = true
        });
    }

    private static async Task CopyDirectoryAsync(string srcDir, string destDir, IProgress<CopyProgress> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(destDir);
        
        foreach (var file in Directory.GetFiles(srcDir))
        {
            ct.ThrowIfCancellationRequested();
            await CopyFileAsync(file, Path.Combine(destDir, Path.GetFileName(file)), progress, ct);
        }
        
        foreach (var dir in Directory.GetDirectories(srcDir))
        {
            ct.ThrowIfCancellationRequested();
            await CopyDirectoryAsync(dir, Path.Combine(destDir, Path.GetDirectoryName(dir)!), progress, ct);
        }
    }
}

public sealed class CopyProgress
{
    public string CurrentFile { get; set; } = "";
    public long TotalBytes { get; set; }
    public long CopiedBytes { get; set; }
    public bool IsComplete { get; set; }
    public double Percent => TotalBytes > 0 ? (double)CopiedBytes / TotalBytes * 100 : 0;
}