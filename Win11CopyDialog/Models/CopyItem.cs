using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Win11CopyDialog.Models;

public enum CopyItemStatus
{
    Queued,
    Copying,
    Paused,
    Done,
    Skipped,
    Error
}

/// <summary>Один файл в операции копирования.</summary>
public sealed class CopyItem : INotifyPropertyChanged
{
    public string FileName { get; }
    public string SourcePath { get; }
    public string DestPath { get; }
    public long SizeBytes { get; }

    private long _copiedBytes;
    public long CopiedBytes
    {
        get => _copiedBytes;
        set { if (_copiedBytes != value) { _copiedBytes = value; OnChanged(); OnChanged(nameof(Progress)); OnChanged(nameof(RemainingBytes)); } }
    }

    private CopyItemStatus _status = CopyItemStatus.Queued;
    public CopyItemStatus Status
    {
        get => _status;
        set { if (_status != value) { _status = value; OnChanged(); OnChanged(nameof(StatusGlyph)); OnChanged(nameof(IsFinished)); } }
    }

    public double Progress => SizeBytes <= 0 ? 100 : CopiedBytes * 100.0 / SizeBytes;
    public long RemainingBytes => Math.Max(0, SizeBytes - CopiedBytes);
    public string SizeText => Helpers.Formatters.Bytes(SizeBytes);
    public bool IsFinished => Status is CopyItemStatus.Done or CopyItemStatus.Skipped or CopyItemStatus.Error;

    public string StatusGlyph => Status switch
    {
        CopyItemStatus.Done => "✔",
        CopyItemStatus.Copying => "➔",
        CopyItemStatus.Paused => "⏸",
        CopyItemStatus.Skipped => "⊘",
        CopyItemStatus.Error => "✖",
        _ => "○"
    };

    public CopyItem(string fileName, long sizeBytes, string sourcePath = "", string destPath = "")
    {
        FileName = fileName;
        SizeBytes = sizeBytes;
        SourcePath = sourcePath;
        DestPath = destPath;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
