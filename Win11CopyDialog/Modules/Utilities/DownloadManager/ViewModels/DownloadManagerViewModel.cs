using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Win11CopyDialog.Modules.Utilities.DownloadManager.Models;
using Win11CopyDialog.Modules.Utilities.DownloadManager.Services;

namespace Win11CopyDialog.Modules.Utilities.DownloadManager.ViewModels
{
    public class DownloadManagerViewModel : INotifyPropertyChanged
    {
        private readonly DatabaseService _dbService;
        private readonly DownloadTaskConfig _config;
        private DownloadItem _selectedDownload;

        public ObservableCollection<DownloadItem> Downloads { get; set; } = new ObservableCollection<DownloadItem>();

        public DownloadItem SelectedDownload
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
            var items = await _dbService.GetAllDownloadsAsync();
            App.Current.Dispatcher.Invoke(() =>
            {
                Downloads.Clear();
                foreach (var item in items)
                {
                    Downloads.Add(item);
                }
            });
        }

        private async Task AddDownloadAsync()
        {
            // For testing: hardcoded URL
            var url = "https://releases.ubuntu.com/22.04.3/ubuntu-22.04.3-desktop-amd64.iso";
            var fileName = "ubuntu-22.04.3-desktop-amd64.iso";
            
            var item = new DownloadItem
            {
                Url = url,
                FileName = fileName,
                SavePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", fileName),
                Status = DownloadStatus.Queued
            };

            await _dbService.SaveDownloadAsync(item);
            App.Current.Dispatcher.Invoke(() => Downloads.Insert(0, item));

            var engine = new DownloadEngine(_dbService, _config);
            engine.ProgressChanged += Engine_ProgressChanged;
            _ = engine.StartDownloadAsync(item);
        }

        private void Engine_ProgressChanged(object sender, DownloadItem e)
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
                _dbService.SaveDownloadAsync(SelectedDownload);
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

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class RelayCommand : ICommand
    {
        private readonly Action<object> _execute;
        private readonly Predicate<object> _canExecute;

        public RelayCommand(Action<object> execute, Predicate<object> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object parameter) => _canExecute == null || _canExecute(parameter);

        public void Execute(object parameter) => _execute(parameter);

        public event EventHandler CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }
    }
}
