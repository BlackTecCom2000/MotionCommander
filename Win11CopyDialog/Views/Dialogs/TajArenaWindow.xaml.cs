using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Win11CopyDialog.Helpers;
using Win11CopyDialog.Modules.TajArena.Models;
using Win11CopyDialog.Modules.TajArena.Services;

namespace Win11CopyDialog.Views.Dialogs;

/// <summary>
/// Окно игровой категории TAJ ARENA.
/// </summary>
/// <remarks>
/// <para>Все числа в окне — измерения файловой системы: объём папки
/// считается заново при каждом открытии и каждом поиске, доля диска —
/// из реальных DriveInfo. Ни одно значение не сохраняется «на память»:
/// устаревший размер был бы именно выдумкой.</para>
///
/// <para>Запуск игры выполняется только на реальном существующем файле
/// типа .exe. Когда кандидатов несколько и автоматический выбор
/// неоднозначен, сервис сознательно возвращает null, и пользователь
/// выбирает приложение сам — навязывать сомнительную эвристику
/// опаснее, чем лишний клик.</para>
/// </remarks>
public sealed partial class TajArenaWindow : Window
{
    private readonly TajArenaState _state;

    /// <summary>
    /// Строки ListView. Отдельная коллекция нужна потому, что игру
    /// пользователь добавляет и когда автопоиск ещё не выполнялся,
    /// и в любой момент: список обязан пополняться живьём.
    /// </summary>
    private readonly ObservableCollection<GameEntry> _rows = new();

    /// <summary>Полный логический список, включая скрытые фильтром.</summary>
    private List<GameEntry> _games = new();

    private System.Windows.Threading.DispatcherTimer? _measureTimer;

    public TajArenaWindow()
    {
        InitializeComponent();

        _state = TajArenaStore.Load();
        GamesListView.ItemsSource = _rows;

        Loaded += async (_, _) => await RunDiscoveryAsync();
        Closed += (_, _) => StopMeasureTimer();
    }

    // ------------------------------------------------------------------
    // Поиск и заполнение списка
    // ------------------------------------------------------------------

    private async Task RunDiscoveryAsync()
    {
        RefreshBtn.IsEnabled = false;
        ScanProgress.Visibility = Visibility.Visible;
        StatusText.Text = "Поиск установленных игр: реестр, библиотеки Steam, Epic Games...";

        List<string> probes;
        try
        {
            // Синхронный поиск вынесен из UI-потока: чтение реестра и
            // обход дисков занимают секунды, и окно не должно «мертнуть».
            var result = await Task.Run(GameDiscoveryService.Discover);
            if (!IsLoaded)
            {
                // Окно могли закрыть, пока шёл поиск: трогать UI нельзя.
                return;
            }

            ApplyDiscovery(result);
            probes = result.Probes;
        }
        catch (Exception ex)
        {
            if (!IsLoaded) return;
            probes = new List<string> { $"автопоиск: {ex.GetType().Name}: {ex.Message}" };
            StatusText.Text = "Ошибка поиска установленных игр — список пуст.";
        }
        finally
        {
            if (IsLoaded)
            {
                RefreshBtn.IsEnabled = true;
                ScanProgress.Visibility = Visibility.Collapsed;
            }
        }

        if (IsLoaded)
        {
            // Честный журнал: не только «найдено 0», но и почему.
            StatusText.Text = _games.Count == 0
                ? "Игры не найдены — наведите на этот текст: почему и как добавить вручную."
                : $"Игр в списке: {_games.Count}. Размеры измеряются в фоне...";

            StatusDetailsTip.Content = string.Join(Environment.NewLine, probes);
        }

        StartMeasureTimer();
    }

    /// <summary>
    /// Собирает финальный список: автопоиск + данные пользователя,
    /// скрытые записи фильтруются, выбранные exe восстанавливаются.
    /// </summary>
    private void ApplyDiscovery(DiscoveryResult result)
    {
        var merged = new Dictionary<string, GameEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var auto in result.Games)
        {
            if (_state.HiddenPaths.Contains(auto.InstallPath, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }
            merged[auto.InstallPath] = auto;
        }

        foreach (var manual in _state.ManualGames)
        {
            // Папку, которую пользователь добавил сам, выбрасывать из
            // списка нельзя, пока она существует: он добавлял её сам.
            if (Directory.Exists(manual.InstallPath))
            {
                merged[manual.InstallPath] = manual;
            }
        }

        var games = merged.Values.ToList();

        foreach (var game in games)
        {
            if (_state.ChosenExes.TryGetValue(game.InstallPath, out var chosen) &&
                File.Exists(chosen))
            {
                game.MainExePath = chosen;
            }
            else if (game.Platform == GamePlatform.Manual &&
                     !string.IsNullOrEmpty(game.MainExePath) && !File.Exists(game.MainExePath))
            {
                // Пользователь выбирал файл, но он исчез: сбрасываем
                // с честным пояснением, а не молча.
                game.MainExePath = null;
                game.Notes = "Выбранный ранее файл больше не существует — укажите его заново.";
            }
        }

        _games = games;
        _rows.Clear();
        foreach (var game in games)
        {
            _rows.Add(game);
        }
        UpdateButtonStates();
    }

    // ------------------------------------------------------------------
    // Измерение размеров (фоном, построчно, без блокировки списка)
    // ------------------------------------------------------------------

    /// <summary>
    /// Измерение идёт по одной игре за тик таймера: цепочка вложенных
    /// async запусков могла бы оставить прогресс «висящим» после
    /// закрытия окна, а таймер останавливается точно и предсказуемо.
    /// </summary>
    private void StartMeasureTimer()
    {
        StopMeasureTimer();

        if (_games.Count == 0) return;

        _measureTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _measureTimer.Tick += async (_, _) => await MeasureNextGame();
        _measureTimer.Start();
    }

    private void StopMeasureTimer()
    {
        _measureTimer?.Stop();
        _measureTimer = null;
    }

    private async Task MeasureNextGame()
    {
        StopMeasureTimer();

        var game = _games.FirstOrDefault(g => g.SizeBytes == null);
        if (game == null)
        {
            if (IsLoaded)
            {
                StatusText.Text = $"Игр в списке: {_games.Count}. Все размеры измерены.";
            }
            return;
        }

        if (IsLoaded)
        {
            var index = _games.IndexOf(game);
            StatusText.Text = $"Измерение: {game.DisplayName} ({index + 1} из {_games.Count})...";
        }

        long size = 0;
        bool partial = false;
        try
        {
            (size, partial) = await Task.Run(() => MeasureDirectorySize(game.InstallPath));
        }
        catch (Exception)
        {
            partial = true;
        }

        if (!IsLoaded) return;

        game.SizeBytes = size;
        if (partial && game.Notes.Length == 0)
        {
            game.Notes = "Часть файлов недоступна для чтения — объём может быть занижен.";
        }

        game.PercentOfDisk = await Task.Run(() => ComputeDiskPercent(game));

        // Следующая игра — в следующем тике: UI остаётся живым даже
        // на очень больших папках.
        if (IsLoaded) StartMeasureTimer();
    }

    /// <summary>
    /// Подсчёт объёма папки по реальным файлам. Второе значение — флаг
    /// «измерение неполное»: хотя бы один файл не прочитался, и молча
    /// показывать заниженное число — нельзя.
    /// </summary>
    private static (long size, bool partial) MeasureDirectorySize(string path)
    {
        long total = 0;
        bool partial = false;

        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            return (0, false);
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*",
                new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = true
                }))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch
                {
                    // Один непрочитанный файл не отменяет измерение,
                    // но честно помечает его неполным.
                    partial = true;
                }
            }
        }
        catch
        {
            // Сам обход не состоялся: ноль плюс честный флаг вместо
            // придуманного числа.
            partial = true;
        }

        return (total, partial);
    }

    /// <summary>
    /// Доля диска: размер игры к реальному объёму диска. Диск может
    /// отсутствовать (сетевой, отключённый) — тогда вместо процента
    /// честно остаётся «—», а не ноль.
    /// </summary>
    private static double? ComputeDiskPercent(GameEntry game)
    {
        try
        {
            if (game.DriveRoot is not string root || game.SizeBytes is not long size)
            {
                return null;
            }

            var drive = new DriveInfo(root);
            if (!drive.IsReady || drive.TotalSize <= 0)
            {
                return null;
            }

            return Math.Round(size * 100.0 / drive.TotalSize, 2);
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Управление списком: поиск заново, добавление, удаление, выбор файла
    // ------------------------------------------------------------------

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        await RunDiscoveryAsync();
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();

        var picker = new CyberFolderPickerDialog("ВЫБОР ПАПКИ ИГРЫ (TAJ ARENA)") { Owner = this };
        if (picker.ShowDialog() != true || string.IsNullOrWhiteSpace(picker.SelectedPath))
        {
            return;
        }

        string folder = GameDiscoveryService.NormalizePath(picker.SelectedPath);
        if (!Directory.Exists(folder))
        {
            StatusText.Text = "Папка не существует — добавление отменено.";
            return;
        }

        if (_games.Any(g => g.InstallPath.Equals(folder, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText.Text = "Эта папка уже в списке.";
            return;
        }

        var entry = new GameEntry
        {
            DisplayName = Path.GetFileName(folder) ?? folder,
            InstallPath = folder,
            Platform = GamePlatform.Manual,
            Notes = GameDiscoveryService.IsLikelyGameFolder(folder)
                ? ""
                : "В папке нет исполняемых файлов: добавлена, но запуск не настроен."
        };
        entry.CandidateExePaths = FindCandidatesForManual(entry);

        // Автовыбор запускаемого файла только в однозначном случае:
        // единственный кандидат без «шумных» имён. Иначе решает
        // пользователь — навязывать эвристику нельзя.
        if (entry.CandidateExePaths.Count == 1)
        {
            entry.MainExePath = entry.CandidateExePaths[0];
        }

        _state.ManualGames.Add(entry);
        SaveState("Не удалось сохранить игровую папку в файл настроек — добавлена только до конца сеанса.");

        _games.Add(entry);
        _rows.Add(entry);
        StatusText.Text = $"Папка добавлена. Размер будет измерен: {entry.DisplayName}.";
        GamesListView.SelectedItem = entry;
        GamesListView.ScrollIntoView(entry);

        StartMeasureTimer();
    }

    private List<string> FindCandidatesForManual(GameEntry entry)
    {
        try
        {
            return Directory.EnumerateFiles(entry.InstallPath, "*.exe",
                    new EnumerationOptions
                    {
                        IgnoreInaccessible = true,
                        RecurseSubdirectories = true,
                        MaxRecursionDepth = 3
                    })
                .Where(f => !IsExecutableNoise(Path.GetFileName(f)))
                .OrderByDescending(f => new FileInfo(f).Length)
                .Take(8)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// Системные и служебные программы, которые никогда не бывают
    /// «главной» программой игры. Список повторяет правило сервиса
    /// поиска: сервис не экспортирует его наружу, а расширение
    /// публичной поверхности ради одной кнопки не оправдано.
    /// </summary>
    private static bool IsExecutableNoise(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return true;

        string lower = fileName.ToLowerInvariant();
        string[] noise =
        {
            "uninstall", "uninst", "setup", "installe", "redist",
            "vcredist", "dxsetup", "dotnet", "crash", "handler",
            "updater", "unitycrash", "eula", "copyright", "readme",
            "directx", "installscript", "bspatch", "patcher"
        };

        return noise.Any(n => lower.Contains(n));
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();

        if (GamesListView.SelectedItem is not GameEntry game) return;

        if (game.Platform == GamePlatform.Manual)
        {
            _state.ManualGames.RemoveAll(g =>
                g.InstallPath.Equals(game.InstallPath, StringComparison.OrdinalIgnoreCase));
            _games.Remove(game);
            _rows.Remove(game);
            StatusText.Text = $"«{game.DisplayName}» удалена из списка. Файлы на диске не тронуты.";
        }
        else
        {
            if (!_state.HiddenPaths.Contains(game.InstallPath, StringComparer.OrdinalIgnoreCase))
            {
                _state.HiddenPaths.Add(game.InstallPath);
            }
            _games.Remove(game);
            _rows.Remove(game);
            StatusText.Text = $"«{game.DisplayName}» скрыта. Вернётся при следующем поиске заново.";
        }

        SaveState("Изменение списка применено только до конца сеанса — сохранить в файл настроек не удалось.");
        UpdateButtonStates();
    }

    private void ChooseExe_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        if (GamesListView.SelectedItem is not GameEntry game) return;

        ChooseExecutable(game);
    }

    private void ChooseExecutable(GameEntry game)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Выбор приложения игры «{game.DisplayName}»",
            Filter = "Исполняемые файлы (*.exe)|*.exe",
            CheckFileExists = true
        };

        try
        {
            if (Directory.Exists(game.InstallPath))
            {
                dialog.InitialDirectory = game.InstallPath;
            }
        }
        catch
        {
            // Некорректный путь не сломает диалог: он откроется в
            // текущей папке пользователя.
        }

        if (dialog.ShowDialog(this) != true) return;

        // Файл вне папки игры — не запрет: пользователь может усилить
        // запуск через внешний лаунчер. Окно честно различает случаи.
        if (!string.IsNullOrEmpty(game.InstallPath) &&
            dialog.FileName.StartsWith(game.InstallPath, StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = $"Приложение выбрано: {Path.GetFileName(dialog.FileName)}";
        }
        else
        {
            StatusText.Text = "Выбранный файл лежит вне папки игры — запомнено как есть.";
        }

        game.MainExePath = dialog.FileName;
        _state.ChosenExes[game.InstallPath] = dialog.FileName;
        SaveState("Выбор приложения применён только до конца сеанса — сохранить не удалось.");
    }

    // ------------------------------------------------------------------
    // Запуск игры
    // ------------------------------------------------------------------

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        if (GamesListView.SelectedItem is GameEntry game)
        {
            LaunchGame(game);
        }
    }

    private void GamesListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GamesListView.SelectedItem is GameEntry game)
        {
            LaunchGame(game);
        }
    }

    private void LaunchGame(GameEntry game)
    {
        // Проверка перед каждым запуском вместо кэша: файл мог быть
        // удалён после последнего поиска.
        if (string.IsNullOrEmpty(game.MainExePath))
        {
            ChooseExecutable(game);
            if (string.IsNullOrEmpty(game.MainExePath)) return;
        }

        if (!File.Exists(game.MainExePath!))
        {
            var reselect = MessageBox.Show(this,
                $"Выбранный файл больше не существует:\n{game.MainExePath}\n\nУказать другой файл?",
                "TAJ ARENA", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (reselect == MessageBoxResult.Yes)
            {
                ChooseExecutable(game);
                if (!string.IsNullOrEmpty(game.MainExePath) && File.Exists(game.MainExePath))
                {
                    LaunchGame(game);
                }
            }
            return;
        }

        try
        {
            var start = new ProcessStartInfo(game.MainExePath!)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(game.MainExePath) ?? game.InstallPath
            };
            Process.Start(start);
            HapticAudio.PlaySuccess();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Не удалось запустить игру:\n{game.MainExePath}\n\n{ex.Message}",
                "TAJ ARENA", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ------------------------------------------------------------------
    // Вспомогательное
    // ------------------------------------------------------------------

    private void GamesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        HapticAudio.PlaySelect();
        UpdateButtonStates();
    }

    private void UpdateButtonStates()
    {
        bool has = GamesListView.SelectedItem is GameEntry;
        PlayBtn.IsEnabled = has;
        RemoveBtn.IsEnabled = has;
        ChooseExeBtn.IsEnabled = has;
    }

    /// <summary>
    /// Сохранение состояния с честным исходом: не сумев записать файл,
    /// окно не притворяется, будто всё сохранено, а сообщает об этом.
    /// </summary>
    private void SaveState(string failureMessage)
    {
        if (TajArenaStore.Save(_state)) return;

        StatusText.Text = failureMessage;
        MessageBox.Show(this, failureMessage, "TAJ ARENA", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void Min_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        WindowState = WindowState.Minimized;
    }

    private void Max_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        if (MaxBtn != null) MaxBtn.Content = WindowState == WindowState.Maximized ? "❐" : "□";
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        Close();
    }
}
