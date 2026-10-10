using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Win11CopyDialog.Modules.OperationsQueue.Models;

namespace Win11CopyDialog.Modules.OperationsQueue.Services;

/// <summary>
/// Единая служба управления очередью операций (копирование, скачивание, архивация, сканирование/очистка).
/// Обеспечивает сквозной мониторинг, паузу, возобновление, отмену, повтор после ошибок и ведение истории.
/// </summary>
public sealed class UnifiedOperationsQueueService
{
    private static readonly Lazy<UnifiedOperationsQueueService> _instance = new(() => new UnifiedOperationsQueueService());
    public static UnifiedOperationsQueueService Instance => _instance.Value;

    public ObservableCollection<QueuedOperationItem> ActiveOperations { get; } = new();
    public ObservableCollection<QueuedOperationItem> HistoryOperations { get; } = new();

    public event EventHandler? QueueChanged;

    private readonly object _lock = new();

    private UnifiedOperationsQueueService() { }

    public QueuedOperationItem Enqueue(
        QueuedOperationType type,
        string title,
        string description,
        Action? pauseCallback = null,
        Action? resumeCallback = null,
        Action? cancelCallback = null,
        Func<Task>? retryCallback = null)
    {
        var item = new QueuedOperationItem
        {
            Type = type,
            Title = title,
            Description = description,
            Status = QueuedOperationStatus.Running,
            PauseCallback = pauseCallback,
            ResumeCallback = resumeCallback,
            CancelCallback = cancelCallback,
            RetryCallback = retryCallback
        };

        RunOnUi(() =>
        {
            lock (_lock)
            {
                ActiveOperations.Insert(0, item);
            }
            QueueChanged?.Invoke(this, EventArgs.Empty);
        });

        return item;
    }

    public void UpdateProgress(string id, double progress, string speedText, string etaText, string? description = null)
    {
        RunOnUi(() =>
        {
            var item = ActiveOperations.FirstOrDefault(o => o.Id == id);
            if (item != null)
            {
                item.Progress = Math.Clamp(progress, 0, 100);
                if (!string.IsNullOrEmpty(speedText)) item.SpeedText = speedText;
                if (!string.IsNullOrEmpty(etaText)) item.EtaText = etaText;
                if (!string.IsNullOrEmpty(description)) item.Description = description;
            }
        });
    }

    public void MarkCompleted(string id)
    {
        RunOnUi(() =>
        {
            var item = ActiveOperations.FirstOrDefault(o => o.Id == id);
            if (item != null)
            {
                item.Status = QueuedOperationStatus.Completed;
                item.Progress = 100;
                item.FinishedAt = DateTime.Now;
                ActiveOperations.Remove(item);
                HistoryOperations.Insert(0, item);
                QueueChanged?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    public void MarkFailed(string id, string errorMessage)
    {
        RunOnUi(() =>
        {
            var item = ActiveOperations.FirstOrDefault(o => o.Id == id);
            if (item != null)
            {
                item.Status = QueuedOperationStatus.Failed;
                item.ErrorMessage = errorMessage;
                item.FinishedAt = DateTime.Now;
                ActiveOperations.Remove(item);
                HistoryOperations.Insert(0, item);
                QueueChanged?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    public void MarkCancelled(string id)
    {
        RunOnUi(() =>
        {
            var item = ActiveOperations.FirstOrDefault(o => o.Id == id);
            if (item != null)
            {
                item.Status = QueuedOperationStatus.Cancelled;
                item.FinishedAt = DateTime.Now;
                ActiveOperations.Remove(item);
                HistoryOperations.Insert(0, item);
                QueueChanged?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    public void Pause(string id)
    {
        var item = ActiveOperations.FirstOrDefault(o => o.Id == id);
        if (item != null && item.Status == QueuedOperationStatus.Running)
        {
            item.Status = QueuedOperationStatus.Paused;
            item.PauseCallback?.Invoke();
        }
    }

    public void Resume(string id)
    {
        var item = ActiveOperations.FirstOrDefault(o => o.Id == id);
        if (item != null && item.Status == QueuedOperationStatus.Paused)
        {
            item.Status = QueuedOperationStatus.Running;
            item.ResumeCallback?.Invoke();
        }
    }

    public void Cancel(string id)
    {
        var item = ActiveOperations.FirstOrDefault(o => o.Id == id);
        if (item != null)
        {
            item.CancelCallback?.Invoke();
            MarkCancelled(id);
        }
    }

    public async Task RetryAsync(string id)
    {
        var item = HistoryOperations.FirstOrDefault(o => o.Id == id);
        if (item != null && item.RetryCallback != null)
        {
            RunOnUi(() =>
            {
                HistoryOperations.Remove(item);
                item.Status = QueuedOperationStatus.Running;
                item.ErrorMessage = "";
                item.Progress = 0;
                ActiveOperations.Insert(0, item);
                QueueChanged?.Invoke(this, EventArgs.Empty);
            });

            try
            {
                await item.RetryCallback();
            }
            catch (Exception ex)
            {
                MarkFailed(item.Id, ex.Message);
            }
        }
    }

    public void ClearHistory()
    {
        RunOnUi(() =>
        {
            HistoryOperations.Clear();
            QueueChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private static void RunOnUi(Action action)
    {
        if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.BeginInvoke(action);
        }
        else
        {
            action();
        }
    }
}
