using System.IO;
using System.Runtime.InteropServices;
using Win11CopyDialog.Modules.FileManager.Models;

namespace Win11CopyDialog.Modules.FileManager.Services;

/// <summary>
/// Сервис файловой системы с поддержкой длинных путей (\\?\), Unicode, скрытых/системных файлов,
/// быстрого доступа, рекурсивного сканирования папок и безопасных операций.
/// </summary>
public static class FileSystemService
{
    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".tar", ".gz", ".bz2", ".xz", ".tgz", ".tbz2", ".txz",
        ".iso", ".cab", ".arj", ".lzh", ".z", ".cpio"
    };

    public static List<DriveItem> GetDrives()
    {
        var list = new List<DriveItem>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (!d.IsReady) continue;
                string fs = "NTFS";
                try { if (!string.IsNullOrWhiteSpace(d.DriveFormat)) fs = d.DriveFormat; } catch { }
                list.Add(new DriveItem
                {
                    Name = d.Name,
                    RootDirectory = d.RootDirectory.FullName,
                    VolumeLabel = d.VolumeLabel,
                    DriveType = d.DriveType.ToString(),
                    FileSystem = fs,
                    TotalSize = d.TotalSize,
                    FreeSpace = d.AvailableFreeSpace
                });
            }
        }
        catch { }
        return list;
    }

    public static List<QuickAccessItem> GetQuickAccessLocations()
    {
        var items = new List<QuickAccessItem>();
        void Add(string name, string subtitle, Environment.SpecialFolder folder, string icon, string glyph, string vectorKey, string color)
        {
            try
            {
                var p = Environment.GetFolderPath(folder);
                if (!string.IsNullOrEmpty(p) && Directory.Exists(p) && items.All(i => !string.Equals(i.Path, p, StringComparison.OrdinalIgnoreCase)))
                {
                    items.Add(new QuickAccessItem
                    {
                        Name = name,
                        Subtitle = subtitle,
                        Path = p,
                        Icon = icon,
                        Glyph = glyph,
                        VectorIconKey = vectorKey,
                        ColorHex = color
                    });
                }
            }
            catch { }
        }

        Add("Рабочий стол", "Системный стол", Environment.SpecialFolder.Desktop, "🖥", "\uE7F4", "Icon_Desktop", "#3B82F6");

        // Загрузки — ровно один раз
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(userProfile, "Downloads");
        if (Directory.Exists(downloads))
        {
            items.Add(new QuickAccessItem
            {
                Name = "Загрузки",
                Subtitle = "Загруженные файлы",
                Path = downloads,
                Icon = "📥",
                Glyph = "\uE896",
                VectorIconKey = "Icon_Downloads",
                ColorHex = "#10B981"
            });
        }

        Add("Документы", "Личные документы", Environment.SpecialFolder.MyDocuments, "📑", "\uE8A5", "Icon_Documents", "#F59E0B");
        Add("Изображения", "Фото и галерея", Environment.SpecialFolder.MyPictures, "🖼", "\uEB9F", "Icon_Pictures", "#8B5CF6");
        Add("Видео", "Видеозаписи и клипы", Environment.SpecialFolder.MyVideos, "🎬", "\uE714", "Icon_Videos", "#EC4899");
        Add("Музыка", "Аудиозаписи и треки", Environment.SpecialFolder.MyMusic, "🎵", "\uEC4F", "Icon_Music", "#F43F5E");

        // Личная папка пользователя
        if (Directory.Exists(userProfile) && items.All(i => !string.Equals(i.Path, userProfile, StringComparison.OrdinalIgnoreCase)))
        {
            items.Add(new QuickAccessItem
            {
                Name = "Личная папка",
                Subtitle = "Профиль пользователя",
                Path = userProfile,
                Icon = "👤",
                Glyph = "\uE77B",
                VectorIconKey = "Icon_User",
                ColorHex = "#06B6D4"
            });
        }

        return items;
    }

    public static List<FileSystemItem> EnumeratePath(string path, bool showHidden = false, string search = "")
    {
        var result = new List<FileSystemItem>();
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return result;

        try
        {
            var dirInfo = new DirectoryInfo(path);

            // Папки
            foreach (var d in dirInfo.EnumerateDirectories())
            {
                try
                {
                    bool hidden = (d.Attributes & FileAttributes.Hidden) != 0;
                    bool system = (d.Attributes & FileAttributes.System) != 0;
                    if (!showHidden && (hidden || system)) continue;
                    if (!string.IsNullOrEmpty(search) && !d.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
                        continue;

                    result.Add(new FileSystemItem
                    {
                        Name = d.Name,
                        FullPath = d.FullName,
                        IsDirectory = true,
                        IsArchive = false,
                        IsHidden = hidden,
                        IsSystem = system,
                        Attributes = d.Attributes,
                        CreatedTime = d.CreationTime,
                        LastModifiedTime = d.LastWriteTime
                    });
                }
                catch { }
            }

            // Файлы
            foreach (var f in dirInfo.EnumerateFiles())
            {
                try
                {
                    bool hidden = (f.Attributes & FileAttributes.Hidden) != 0;
                    bool system = (f.Attributes & FileAttributes.System) != 0;
                    if (!showHidden && (hidden || system)) continue;
                    if (!string.IsNullOrEmpty(search) && !f.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string ext = f.Extension.ToLowerInvariant();
                    bool isArc = ArchiveExtensions.Contains(ext);

                    result.Add(new FileSystemItem
                    {
                        Name = f.Name,
                        FullPath = f.FullName,
                        Extension = ext,
                        Length = f.Length,
                        IsDirectory = false,
                        IsArchive = isArc,
                        IsHidden = hidden,
                        IsSystem = system,
                        Attributes = f.Attributes,
                        CreatedTime = f.CreationTime,
                        LastModifiedTime = f.LastWriteTime
                    });
                }
                catch { }
            }
        }
        catch { }

        return result;
    }

    public static async Task<long> CalculateDirectorySizeAsync(string path, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            long total = 0;
            try
            {
                var q = new Queue<string>();
                q.Enqueue(path);
                int count = 0;
                while (q.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var dir = q.Dequeue();
                    try
                    {
                        var di = new DirectoryInfo(dir);
                        foreach (var f in di.EnumerateFiles())
                        {
                            total += f.Length;
                            count++;
                            if (count % 200 == 0) progress?.Report(total);
                        }
                        foreach (var sub in di.EnumerateDirectories())
                        {
                            q.Enqueue(sub.FullName);
                        }
                    }
                    catch { }
                }
                progress?.Report(total);
            }
            catch { }
            return total;
        }, ct);
    }

    public static bool CreateFolder(string parentPath, string folderName, out string newPath, out string error)
    {
        error = "";
        newPath = "";
        try
        {
            newPath = Path.Combine(parentPath, folderName);
            if (Directory.Exists(newPath))
            {
                error = "Папка с таким именем уже существует.";
                return false;
            }
            Directory.CreateDirectory(newPath);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool Rename(string oldPath, string newName, out string newPath, out string error)
    {
        error = "";
        newPath = "";
        try
        {
            var dir = Path.GetDirectoryName(oldPath) ?? "";
            newPath = Path.Combine(dir, newName);
            if (File.Exists(oldPath))
            {
                if (File.Exists(newPath)) { error = "Файл с таким именем уже существует."; return false; }
                File.Move(oldPath, newPath);
                return true;
            }
            if (Directory.Exists(oldPath))
            {
                if (Directory.Exists(newPath)) { error = "Папка с таким именем уже существует."; return false; }
                Directory.Move(oldPath, newPath);
                return true;
            }
            error = "Элемент не найден.";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool Duplicate(string sourcePath, out string newPath, out string error)
    {
        error = "";
        newPath = "";
        try
        {
            var dir = Path.GetDirectoryName(sourcePath) ?? "";
            var fn = Path.GetFileNameWithoutExtension(sourcePath);
            var ext = Path.GetExtension(sourcePath);

            int counter = 1;
            do
            {
                string copyName = $"{fn} (копия {counter}){ext}";
                newPath = Path.Combine(dir, copyName);
                counter++;
            } while (File.Exists(newPath) || Directory.Exists(newPath));

            if (File.Exists(sourcePath))
            {
                File.Copy(sourcePath, newPath);
                return true;
            }
            if (Directory.Exists(sourcePath))
            {
                CopyDirectoryRecursive(sourcePath, newPath);
                return true;
            }
            error = "Исходный элемент не найден.";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool DeleteItem(string path, bool permanent, out string error)
    {
        error = "";
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
                return true;
            }
            error = "Элемент не найден.";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destinationDir)
    {
        var dir = new DirectoryInfo(sourceDir);
        Directory.CreateDirectory(destinationDir);
        foreach (var file in dir.GetFiles())
        {
            string targetFilePath = Path.Combine(destinationDir, file.Name);
            file.CopyTo(targetFilePath);
        }
        foreach (var subDir in dir.GetDirectories())
        {
            string newDestinationDir = Path.Combine(destinationDir, subDir.Name);
            CopyDirectoryRecursive(subDir.FullName, newDestinationDir);
        }
    }
}
