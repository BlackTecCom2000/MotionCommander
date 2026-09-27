using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Win11CopyDialog.Modules.Utilities.DownloadManager.Models;
using Win11CopyDialog.Modules.Utilities.DownloadManager.Services;

namespace Win11CopyDialog.Modules.Utilities.DownloadManager.ViewModels
{
    public class DownloadManagerViewModel : INotifyPropertyChanged
    {
        private readonly DatabaseService _dbService;
        private readonly DownloadTaskConfig _config;
        // Nullable: оба поля изначально не заданы (до выбора пользователем),
        // а проверки в командах построены на сравнении с null.
        private DownloadItem? _selectedDownload;

        private string? _newUrl;
        
        // Публичные свойства остаются непустыми для простоты биндинга XAML,
        // а внутри возвращают пустую строку / null через null-forgiving оператор.
        public string NewUrl
        {
            get => _newUrl ?? "";
            set
            {
                _newUrl = value;
                OnPropertyChanged();
            }
        }

        public ObservableCollection<DownloadItem> Downloads { get; set; } = new ObservableCollection<DownloadItem>();

        public DownloadItem? SelectedDownload
        {
            get => _selectedDownload;
            set
            {
                _selectedDownload = value;
                OnPropertyChanged();
            }
        }

        public ICommand AddDownloadCommand { get; }
        public ICommand PauseDownloadCommand { get; }
        public ICommand ResumeDownloadCommand { get; }
        public ICommand DeleteDownloadCommand { get; }

        public DownloadManagerViewModel()
        {
            _dbService = new DatabaseService();
            _config = new DownloadTaskConfig();

            AddDownloadCommand = new RelayCommand(async _ => await AddDownloadAsync());
            PauseDownloadCommand = new RelayCommand(_ => PauseDownload(), _ => SelectedDownload != null);
            ResumeDownloadCommand = new RelayCommand(async _ => await ResumeDownloadAsync(), _ => SelectedDownload != null);
            DeleteDownloadCommand = new RelayCommand(async _ => await DeleteDownloadAsync(), _ => SelectedDownload != null);

            LoadDownloadsAsync().ConfigureAwait(false);
        }

        private async Task LoadDownloadsAsync()
        {
            try
            {
                var items = await _dbService.GetAllDownloadsAsync().ConfigureAwait(false);
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher != null)
                {
                    dispatcher.Invoke(() =>
                    {
                        Downloads.Clear();
                        foreach (var item in items)
                        {
                            Downloads.Add(item);
                        }
                    });
                }
            }
            catch { }
        }

        private async Task AddDownloadAsync()
        {
            if (string.IsNullOrWhiteSpace(NewUrl)) return;
            
            var url = NewUrl.Trim();
            
            // Extract filename from URL or use a default one
            var uri = new Uri(url);
            var fileName = Path.GetFileName(uri.LocalPath);
            if (string.IsNullOrEmpty(fileName)) fileName = "download_" + DateTime.Now.Ticks + ".bin";
            
            var item = new DownloadItem
            {
                Url = url,
                FileName = fileName,
                SavePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", fileName),
                Status = DownloadStatus.Queued
            };

            await _dbService.SaveDownloadAsync(item);
            App.Current.Dispatcher.Invoke(() => Downloads.Insert(0, item));
            
            NewUrl = string.Empty;

            var engine = new DownloadEngine(_dbService, _config);
            engine.ProgressChanged += Engine_ProgressChanged;
            _ = engine.StartDownloadAsync(item);
        }

        private void Engine_ProgressChanged(object? sender, DownloadItem e)
        {
            // Simple notification update
            var item = Downloads.FirstOrDefault(x => x.Id == e.Id);
            if (item != null)
            {
                // In a real MVVM setup, DownloadItem should implement INotifyPropertyChanged
                // For now, we refresh the UI slightly differently or assume it's bound.
            }
        }

        private void PauseDownload()
        {
            if (SelectedDownload != null)
            {
                SelectedDownload.Status = DownloadStatus.Paused;

                // Явный отброс задачи: раньше вызов SaveDownloadAsync не был
                // ожидаемым, что давало предупреждение CS4014 и означало
                // «продолжить, не дожидаясь сохранения» — ошибки БД терялись.
                _ = _dbService.SaveDownloadAsync(SelectedDownload);
                // In a full implementation, you'd keep track of DownloadEngine instances to call Cancel on their CancellationTokenSources
            }
        }

        private async Task ResumeDownloadAsync()
        {
            if (SelectedDownload != null && SelectedDownload.Status == DownloadStatus.Paused)
            {
                SelectedDownload.Status = DownloadStatus.Queued;
                var engine = new DownloadEngine(_dbService, _config);
                engine.ProgressChanged += Engine_ProgressChanged;
                await engine.StartDownloadAsync(SelectedDownload);
            }
        }

        private async Task DeleteDownloadAsync()
        {
            if (SelectedDownload != null)
            {
                var item = SelectedDownload;
                await _dbService.DeleteDownloadAsync(item.Id);
                Downloads.Remove(item);
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// Команда WPF с поддержкой асинхронного выполнения.
    ///
    /// <para>Раньше здесь был <c>Action&lt;object&gt;</c>, и вызовы вида
    /// <c>new RelayCommand(async _ =&gt; await AddDownloadAsync())</c>
    /// компилировались как <c>async void</c>: любое исключение из
    /// AddDownloadAsync попадало в SynchronizationContext, а не к вызывающему,
    /// и команда считалась выполненной мгновенно, до реального результата.</para>
    /// </summary>
    public class RelayCommand : ICommand
    {
        private readonly Func<object?, Task> _executeAsync;
        private readonly Func<object?, bool>? _canExecute;

        public RelayCommand(Func<object?, Task> executeAsync, Func<object?, bool>? canExecute = null)
        {
            _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
            _canExecute = canExecute;
        }

        public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
            : this(p => { execute(p); return Task.CompletedTask; }, canExecute)
        {
        }

        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object? parameter) => _canExecute == null || _canExecute(parameter);

        /// <summary>
        /// ICommand.Execute возвращает void, поэтому асинхронная работа
        /// запускается через ForgetSafe: исключения логируются, а не теряются
        /// и не превращаются в фатальный async void.
        /// </summary>
        public void Execute(object? parameter) => _executeAsync(parameter).ForgetSafe();

        public bool RaiseCanExecuteChanged() => true;
    }

    /// <summary>Оборачивает fire-and-forget задачу, не теряя исключения.</summary>
    internal static class TaskExtensions
    {
        public static async void ForgetSafe(this Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Отмена — штатный сценарий.
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Async command failed: {ex}");
            }
        }
    }
}
