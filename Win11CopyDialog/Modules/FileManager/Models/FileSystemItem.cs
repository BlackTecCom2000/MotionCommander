using System.IO;
using Win11CopyDialog.Helpers;

namespace Win11CopyDialog.Modules.FileManager.Models;

/// <summary>
/// Представляет файл, директорию или элемент внутри архива с полной метаинформацией,
/// форматированными размерами, временными метками и глифами типов файлов.
/// </summary>
public sealed class FileSystemItem
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public long Length { get; set; }
    public long PackedLength { get; set; }
    public bool IsDirectory { get; set; }
    public bool IsArchive { get; set; }
    public bool IsHidden { get; set; }
    public bool IsSystem { get; set; }
    public FileAttributes Attributes { get; set; }
    public DateTime CreatedTime { get; set; }
    public DateTime LastModifiedTime { get; set; }
    public string CrcHex { get; set; } = string.Empty;

    public string SizeFormatted => IsDirectory ? "<ПАПКА>" : Formatters.Bytes(Length);
    public string PackedSizeFormatted => PackedLength > 0 ? Formatters.Bytes(PackedLength) : "—";
    public string CompressionRatioFormatted
    {
        get
        {
            if (IsDirectory || Length <= 0 || PackedLength <= 0) return "—";
            double ratio = (double)PackedLength / Length * 100.0;
            return $"{ratio:0.0}%";
        }
    }

    public string DateModifiedFormatted => LastModifiedTime != DateTime.MinValue
        ? LastModifiedTime.ToString("dd.MM.yyyy HH:mm") : "—";

    public string AttributesFormatted
    {
        get
        {
            var s = "";
            if ((Attributes & FileAttributes.ReadOnly) != 0) s += "R";
            if ((Attributes & FileAttributes.Hidden) != 0) s += "H";
            if ((Attributes & FileAttributes.System) != 0) s += "S";
            if ((Attributes & FileAttributes.Archive) != 0) s += "A";
            return string.IsNullOrEmpty(s) ? "—" : s;
        }
    }

    public string IconGlyph => ResolveGlyph(Name, Extension, IsDirectory, IsArchive);

    public static string ResolveGlyph(string name, string ext, bool isDir, bool isArchive)
    {
        if (isArchive) return "🗜";
        if (isDir) return "📁";
        ext = (ext ?? Path.GetExtension(name)).ToLowerInvariant();
        return ext switch
        {
            ".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".bz2" or ".xz" or ".iso" or ".cab" => "🗜",
            ".exe" or ".msi" or ".bat" or ".cmd" or ".ps1" => "⚙",
            ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".webm" => "🎬",
            ".mp3" or ".flac" or ".wav" or ".m4a" or ".ogg" or ".aac" => "🎵",
            ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp" or ".svg" => "🖼",
            ".pdf" => "📕",
            ".doc" or ".docx" or ".rtf" or ".odt" => "📘",
            ".xls" or ".xlsx" or ".csv" => "📗",
            ".ppt" or ".pptx" => "📙",
            ".txt" or ".log" or ".md" or ".json" or ".xml" or ".yaml" or ".yml" => "📄",
            ".cs" or ".cpp" or ".c" or ".h" or ".py" or ".js" or ".ts" or ".html" or ".css" => "💻",
            ".dll" or ".sys" or ".ini" or ".cfg" => "🔧",
            _ => "📄"
        };
    }
}
