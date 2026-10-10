using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Win11CopyDialog.Helpers;
using Win11CopyDialog.Modules.Utilities.DownloadManager.Models;
using Win11CopyDialog.Modules.Utilities.DownloadManager.Services;

namespace Win11CopyDialog.Modules.Utilities.DownloadManager.ViewModels
{
    public class DownloadManagerViewModel : INotifyPropertyChanged
    {
        private readonly DatabaseService _dbService;
        private readonly DownloadTaskConfig _config;

        /// <summary>
        /// Активные движки по идентификатору загрузки.
        ///
        /// <para>Раньше этот словарь не существовал: экземпляр DownloadEngine
        /// создавался и тут же терялся, поэтому команда «Пауза» физически не
        /// могла отменить CancellationTokenSource и просто меняла надпись
        /// на «Приостановлено», оставляя передачу идти. «Удалить» тоже не
        /// останавливала загрузку — запись продолжалась в файл на диске.</para>
        ///
        /// <para>Держать движок нужно и для того, чтобы сборщик мусора не
        /// уничтожил объект посреди активной передачи.</para>
        /// </summary>
        private readonly Dictionary<string, DownloadEngine> _activeEngines = new(StringComparer.Ordinal);
        private readonly object _engineLock = new();

        private DownloadItem? _selectedDownload;

        private string? _newUrl;
        private string _statusMessage = "";

        /// <summary>Сообщение о последней операции: ошибка URL, отказ БД и т.п.</summary>
        public string StatusMessage
        {
            get => _statusMessage;
            private set { _statusMessage = value; OnPropertyChanged(); }
        }

        public string NewUrl
        {
            get => _newUrl ?? "";
            set
            {
                if (_newUrl == value) return;
                _newUrl = value;
                OnPropertyChanged();
                if (!string.IsNullOrWhiteSpace(value)) StatusMessage = "";
            }
        }

        public ObservableCollection<DownloadItem> Downloads { get; } = new();

        public DownloadItem? SelectedDownload
        {
            get => _selectedDownload;
            set
            {
                if (ReferenceEquals(_selectedDownload, value)) return;
                _selectedDownload = value;
                OnPropertyChanged();
                RefreshCommandStates();
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

            AddDownloadCommand = new RelayCommand(_ => AddDownloadAsync().ForgetSafe());
            PauseDownloadCommand = new RelayCommand(_ => { PauseDownload().ForgetSafe(); return Task.CompletedTask; },
                _ => CanPause);
            ResumeDownloadCommand = new RelayCommand(_ => ResumeDownloadAsync().ForgetSafe(), _ => CanResume);
            DeleteDownloadCommand = new RelayCommand(_ => DeleteDownloadAsync().ForgetSafe(), _ => CanDelete);

            // Раньше здесь стояло LoadDownloadsAsync().ConfigureAwait(false).
            // ConfigureAwait(false) на запущенной, но не ожидаемой задаче
            // ничего не меняет — результат всё равно терялся, а список
            // мог остаться пустым к моменту, когда пользователь на него смотрит.
            LoadDownloadsAsync().ForgetSafe();
        }

        private bool CanPause =>
            SelectedDownload is { } d && d.Status == DownloadStatus.Downloading && IsActive(d);

        private bool CanResume =>
            SelectedDownload is { } d &&
            (d.Status == DownloadStatus.Paused || d.Status == DownloadStatus.Failed) && !IsActive(d);

        private bool CanDelete => SelectedDownload != null;

        private bool IsActive(DownloadItem item)
        {
            lock (_engineLock)
                return _activeEngines.ContainsKey(item.Id);
        }

        private void RefreshCommandStates() => CommandManager.InvalidateRequerySuggested();

        // ================= Загрузка списка =================

        private async Task LoadDownloadsAsync()
        {
            try
            {
                var items = await _dbService.GetAllDownloadsAsync().ConfigureAwait(false);
                foreach (var item in items)
                {
                    // Синхронизация состояния загрузки с файловой системой после перезапуска приложения
                    if (item.Status != DownloadStatus.Completed)
                    {
                        bool changed = false;
                        foreach (var segment in item.Segments)
                        {
                            if (segment.Status == SegmentStatus.Downloading)
                            {
                                segment.Status = SegmentStatus.Pending;
                                changed = true;
                            }

                            string partPath = $"{item.SavePath}.part{segment.Index}";
                            if (File.Exists(partPath))
                            {
                                long diskLen = new FileInfo(partPath).Length;
                                long expectedLen = segment.EndPosition - segment.StartPosition + 1;
                                if (expectedLen > 0 && diskLen >= expectedLen)
                                {
                                    if (segment.Status != SegmentStatus.Completed || segment.BytesDownloaded != expectedLen)
                                    {
                                        segment.Status = SegmentStatus.Completed;
                                        segment.BytesDownloaded = expectedLen;
                                        changed = true;
                                    }
                                }
                                else if (expectedLen > 0 && diskLen > 0)
                                {
                                    if (segment.BytesDownloaded != diskLen)
                                    {
                                        segment.BytesDownloaded = diskLen;
                                        changed = true;
                                    }
                                }
                            }
                            else if (segment.BytesDownloaded > 0)
                            {
                                segment.BytesDownloaded = 0;
                                segment.Status = SegmentStatus.Pending;
                                changed = true;
                            }
                        }

                        if (item.Segments.Count > 0)
                        {
                            item.BytesDownloaded = item.Segments.Sum(s => s.BytesDownloaded);
                        }

                        if (changed || item.Status is DownloadStatus.Downloading or DownloadStatus.Verifying)
                        {
                            if (item.Status is DownloadStatus.Downloading or DownloadStatus.Verifying)
                            {
                                item.Status = DownloadStatus.Paused;
                                item.Speed = 0;
                            }
                            await _dbService.SaveDownloadAsync(item).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        if (item.Segments.Count > 0 && item.BytesDownloaded == 0)
                        {
                            item.BytesDownloaded = item.TotalBytes;
                        }
                    }
                }

                void Apply()
                {
                    Downloads.Clear();
                    foreach (var item in items) Downloads.Add(item);
                }

                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null || dispatcher.CheckAccess()) Apply();
                else await dispatcher.InvokeAsync(Apply);
            }
            catch (Exception ex)
            {
                StatusMessage = "Не удалось прочитать список загрузок: " + ex.Message;
            }
        }

        // ================= Добавление =================

        private async Task AddDownloadAsync()
        {
            string raw = NewUrl.Trim();
            if (raw.Length == 0) return;

            // Раньше здесь стоял голый new Uri(url). Любой неверный ввод
            // выбрасывал UriFormatException прямо из команды, задача
            // отменялась, и пользователь не получал НИКАКОГО сообщения:
            // нажатие просто ничего не делало.
            if (!TryNormalizeUrl(raw, out string url, out string? error))
            {
                StatusMessage = error!;
                return;
            }

            string fileName = DeriveFileName(url);
            string? folder = ResolveDownloadFolder();
            if (folder == null)
            {
                StatusMessage = "Не удалось определить папку для загрузок. Укажите путь в настройках.";
                return;
            }

            var item = new DownloadItem
            {
                Url = url,
                FileName = fileName,
                SavePath = Path.Combine(folder, fileName),
                Status = DownloadStatus.Queued
            };

            try
            {
                await _dbService.SaveDownloadAsync(item).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                StatusMessage = "Не удалось сохранить загрузку в базу: " + ex.Message;
                return;
            }

            void Insert() => Downloads.Insert(0, item);
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) Insert();
            else await dispatcher.InvokeAsync(Insert);

            NewUrl = string.Empty;
            StatusMessage = "Загрузка добавлена: " + fileName;
            await StartEngineAsync(item).ConfigureAwait(false);
            RefreshCommandStates();
        }

        /// <summary>Проверяет адрес и приводит его к http/https с явным сообщением об ошибке.</summary>
        private static bool TryNormalizeUrl(string raw, out string url, out string? error)
        {
            url = "";
            error = null;

            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            {
                error = $"«{raw}» не является корректным адресом.";
                return false;
            }

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            {
                error = $"Схема «{uri.Scheme}» не поддерживается. Используйте http или https.";
                return false;
            }

            if (string.IsNullOrEmpty(uri.Host))
            {
                error = "В адресе не указан узел.";
                return false;
            }

            url = uri.ToString();
            return true;
        }

        private static string DeriveFileName(string url)
        {
            string name;
            try
            {
                var uri = new Uri(url);
                name = Path.GetFileName(uri.LocalPath);
            }
            catch
            {
                name = "";
            }

            // Имя из URL может содержать недопустимые для файла символы
            // (экранирование, двоеточие из Content-Disposition и т.п.).
            foreach (char bad in Path.GetInvalidFileNameChars())
                name = name.Replace(bad, '_');

            if (string.IsNullOrWhiteSpace(name))
                name = "download_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".bin";

            // Защита от выхода за пределы папки: имя вида «..\..\x» недопустимо.
            name = Path.GetFileName(name);
            return string.IsNullOrWhiteSpace(name) ? "download.bin" : name;
        }

        /// <summary>
        /// Папка загрузок. Раньше жёстко использовалась
        /// %UserProfile%\Downloads, которая существует не у всех локалей
        /// и не у всех профилей, и её наличие никогда не проверялось.
        /// </summary>
        private static string? ResolveDownloadFolder()
        {
            foreach (var special in new[]
            {
                Environment.SpecialFolder.UserProfile,
                Environment.SpecialFolder.MyDocuments
            })
            {
                string? profile = Environment.GetFolderPath(special);
                if (string.IsNullOrEmpty(profile)) continue;

                string candidate = Path.Combine(profile, "Downloads");
                try
                {
                    Directory.CreateDirectory(candidate);
                    if (AppPaths.IsDirectoryWritable(candidate)) return candidate;
                }
                catch { /* пробуем следующий вариант */ }
            }

            try
            {
                Directory.CreateDirectory(AppPaths.WritableDataDirectory);
                if (AppPaths.IsDirectoryWritable(AppPaths.WritableDataDirectory))
                    return AppPaths.WritableDataDirectory;
            }
            catch { }

            return null;
        }

        // ================= Управление =================

        private async Task StartEngineAsync(DownloadItem item)
        {
            var engine = new DownloadEngine(_dbService, _config);

            lock (_engineLock)
            {
                // Если по ошибке движок уже есть — не запускаем второй,
                // иначе две передачи писали бы в один файл.
                if (_activeEngines.ContainsKey(item.Id)) return;
                _activeEngines[item.Id] = engine;
            }

            engine.ProgressChanged += OnEngineProgress;
            engine.DownloadCompleted += OnEngineCompleted;
            engine.DownloadFailed += OnEngineFailed;

            try
            {
                await engine.StartDownloadAsync(item).ConfigureAwait(false);
            }
            finally
            {
                lock (_engineLock)
                {
                    _activeEngines.Remove(item.Id);
                    engine.ProgressChanged -= OnEngineProgress;
                    engine.DownloadCompleted -= OnEngineCompleted;
                    engine.DownloadFailed -= OnEngineFailed;
                }
                RefreshCommandStates();
            }
        }

        /// <summary>
        /// Пауза. Раньше меняла только надпись на «Приостановлено», не отменяя
        /// передачу: экземпляр движка был потерян, и CancellationTokenSource
        /// оставался нетронутым. Теперь отменяем по-настоящему; состояние
        /// «Приостановлено» выставляет сам движок в обработчике отмены.
        /// </summary>
        private async Task PauseDownload()
        {
            var item = SelectedDownload;
            if (item == null) return;

            DownloadEngine? engine;
            lock (_engineLock)
                _activeEngines.TryGetValue(item.Id, out engine);

            if (engine == null)
            {
                StatusMessage = "Эта загрузка сейчас не выполняется.";
                return;
            }

            await engine.PauseDownloadAsync().ConfigureAwait(false);
            RefreshCommandStates();
        }

        private async Task ResumeDownloadAsync()
        {
            var item = SelectedDownload;
            if (item == null) return;

            if (IsActive(item))
            {
                StatusMessage = "Загрузка уже выполняется.";
                return;
            }

            StatusMessage = "Продолжение: " + item.FileName;
            await StartEngineAsync(item).ConfigureAwait(false);
            RefreshCommandStates();
        }

        /// <summary>
        /// Удаление. Раньше удаляло строку из базы и из списка, но НЕ останавливало
        /// идущую передачу — движок продолжал писать файл на диск уже после
        /// того, как загрузка исчезла из интерфейса.
        /// </summary>
        private async Task DeleteDownloadAsync()
        {
            var item = SelectedDownload;
            if (item == null) return;

            DownloadEngine? engine;
            lock (_engineLock)
                _activeEngines.TryGetValue(item.Id, out engine);

            if (engine != null)
            {
                await engine.PauseDownloadAsync().ConfigureAwait(false);

                // Даём отмене дойти до конца, иначе удаление из базы может
                // перезаписать статус, который движок запишет в обработчике отмены.
                await Task.Delay(150).ConfigureAwait(false);
            }

            try
            {
                await _dbService.DeleteDownloadAsync(item.Id).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                StatusMessage = "Не удалось удалить запись: " + ex.Message;
            }

            void Remove()
            {
                Downloads.Remove(item);
                if (ReferenceEquals(SelectedDownload, item)) SelectedDownload = null;
            }
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) Remove();
            else await dispatcher.InvokeAsync(Remove);

            RefreshCommandStates();
        }

        // ================= События движка =================

        /// <summary>
        /// Раньше тело метода было полностью пустым. Само по себе это не
        /// ломало прогресс: DownloadItem реализует INotifyPropertyChanged,
        /// а движок сам вызывает OnPropertyChanged, так что полосы обновляются.
        /// Сейчас метод держит единый статус для интерфейса и следит, чтобы
        /// элемент не «потерялся» в коллекции.
        /// </summary>
        private void OnEngineProgress(object? sender, DownloadItem item)
        {
            if (Downloads.Contains(item)) return;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
                Downloads.Insert(0, item);
            else
                _ = dispatcher.InvokeAsync(() => Downloads.Insert(0, item));
        }

        private void OnEngineCompleted(object? sender, DownloadItem item) => Report(item, $"Готово: {item.FileName}", true);

        private void OnEngineFailed(object? sender, DownloadItem item) =>
            Report(item, $"Ошибка загрузки «{item.FileName}»: {item.ErrorMessage}", false);

        private void Report(DownloadItem item, string message, bool success)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) StatusMessage = message;
            else _ = dispatcher.InvokeAsync(() => StatusMessage = message);

            try { Helpers.HapticAudio.PlaySuccess(); }
            catch { /* звук не критичен */ }
        }

        // ================= Уведомления =================

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
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

        public bool RaiseCanExecuteChanged()
        {
            CommandManager.InvalidateRequerySuggested();
            return true;
        }
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
