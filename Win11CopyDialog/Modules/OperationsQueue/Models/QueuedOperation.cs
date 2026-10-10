using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Win11CopyDialog.Modules.OperationsQueue.Models;

public enum QueuedOperationType
{
    Copy,       // 📁 Копирование / Перемещение
    Download,   // 🌐 Загрузка из сети
    Archive,    // 📦 Архивация / Распаковка
    Scan        // 🛡 Сканирование / Очистка
}

public enum QueuedOperationStatus
{
    Queued,
    Running,
    Paused,
    Completed,
    Failed,
    Cancelled
}

public sealed class QueuedOperationItem : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public QueuedOperationType Type { get; set; } = QueuedOperationType.Copy;
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";

    private double _progress;
    public double Progress
    {
        get => _progress;
        set { _progress = value; OnPropertyChanged(); OnPropertyChanged(nameof(ProgressInt)); }
    }
    public int ProgressInt => (int)Math.Clamp(Math.Round(Progress), 0, 100);

    private string _speedText = "";
    public string SpeedText
    {
        get => _speedText;
        set { _speedText = value; OnPropertyChanged(); }
    }

    private string _etaText = "";
    public string EtaText
    {
        get => _etaText;
        set { _etaText = value; OnPropertyChanged(); }
    }

    private QueuedOperationStatus _status = QueuedOperationStatus.Queued;
    public QueuedOperationStatus Status
    {
        get => _status;
        set
        {
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusBadgeColorHex));
            OnPropertyChanged(nameof(CanPause));
            OnPropertyChanged(nameof(CanResume));
            OnPropertyChanged(nameof(CanCancel));
            OnPropertyChanged(nameof(CanRetry));
            OnPropertyChanged(nameof(IsRunning));
        }
    }

    public string StatusText => Status switch
    {
        QueuedOperationStatus.Queued => "В очереди",
        QueuedOperationStatus.Running => "Выполняется",
        QueuedOperationStatus.Paused => "Пауза",
        QueuedOperationStatus.Completed => "Завершено",
        QueuedOperationStatus.Failed => "Ошибка",
        QueuedOperationStatus.Cancelled => "Отменено",
        _ => "Неизвестно"
    };

    public string StatusBadgeColorHex => Status switch
    {
        QueuedOperationStatus.Queued => "#94A3B8",
        QueuedOperationStatus.Running => "#3B82F6",
        QueuedOperationStatus.Paused => "#F59E0B",
        QueuedOperationStatus.Completed => "#10B981",
        QueuedOperationStatus.Failed => "#EF4444",
        QueuedOperationStatus.Cancelled => "#64748B",
        _ => "#94A3B8"
    };

    public string TypeIcon => Type switch
    {
        QueuedOperationType.Copy => "📁",
        QueuedOperationType.Download => "🌐",
        QueuedOperationType.Archive => "📦",
        QueuedOperationType.Scan => "🛡",
        _ => "⚡"
    };

    public string TypeDisplayName => Type switch
    {
        QueuedOperationType.Copy => "Копирование",
        QueuedOperationType.Download => "Загрузка",
        QueuedOperationType.Archive => "Архив",
        QueuedOperationType.Scan => "Сканирование",
        _ => "Операция"
    };

    private string _errorMessage = "";
    public string ErrorMessage
    {
        get => _errorMessage;
        set { _errorMessage = value; OnPropertyChanged(); }
    }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? FinishedAt { get; set; }

    public bool IsRunning => Status == QueuedOperationStatus.Running;
    public bool CanPause => Status == QueuedOperationStatus.Running;
    public bool CanResume => Status == QueuedOperationStatus.Paused;
    public bool CanCancel => Status == QueuedOperationStatus.Running || Status == QueuedOperationStatus.Paused || Status == QueuedOperationStatus.Queued;
    public bool CanRetry => Status == QueuedOperationStatus.Failed || Status == QueuedOperationStatus.Cancelled;

    public Action? PauseCallback { get; set; }
    public Action? ResumeCallback { get; set; }
    public Action? CancelCallback { get; set; }
    public Func<Task>? RetryCallback { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
