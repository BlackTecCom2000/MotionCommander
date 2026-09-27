using System.Windows;
using Win11CopyDialog.Models;

namespace Win11CopyDialog;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Явный переключатель портативного режима имеет приоритет над автоопределением.
        if (e.Args.Contains("--portable"))
            Helpers.AppPaths.SetPortable(true, out _);

        // Каталоги для изменяемого состояния. В Program Files писать рядом с exe
        // нельзя, поэтому staging/логи/бенчмарки живут в профиле пользователя.
        Helpers.AppPaths.EnsureDirectories();

        CleanupOldFiles();

        // crash.log РАНЬШЕ писался рядом с exe. В Program Files запись запрещена,
        // лог молча не создавался, а диалог утверждал, что он записан.
        string crashLog = Helpers.AppPaths.CrashLogFile;
        AppDomain.CurrentDomain.UnhandledException += (s, ev) => {
            try { System.IO.File.AppendAllText(crashLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] (Domain)\n{ev.ExceptionObject}\n\n"); } catch {}
        };

        DispatcherUnhandledException += (s, ev) => {
            try { System.IO.File.AppendAllText(crashLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] (UI)\n{ev.Exception}\n\n"); } catch {}

            // Раньше обработчик только писал лог и НЕ устанавливал ev.Handled = true.
            // Из-за этого ЛЮБОЕ исключение в обработчике кнопки (даже в async void)
            // приводило к фатальному завершению приложения — а их в проекте десятки.
            // Теперь необрабоченные исключения на UI-потоке показываются
            // пользователю и не убивают процесс.
            MessageBox.Show(
                $"Непредвиденная ошибка интерфейса:\n\n{ev.Exception?.Message}\n\n" +
                $"Подробности записаны в файл:`r`n{crashLog}",
                "Motion Commander — ошибка",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            ev.Handled = true;
        };

        int seamlessIdx = Array.IndexOf(e.Args, "--seamless-update");
        if (seamlessIdx >= 0)
        {
            int oldPid = 0;
            string? stateFilePath = null;

            // Раньше первая ветка требовала seamlessIdx + 2 (два аргумента после
            // флага), хотя читала ПЕРВЫЙ из них. В результате при передаче
            // «--seamless-update <pid> <file>» неудачным int.TryParse ветка
            // не срабатывала, и stateFilePath становился строкой PID —
            // File.Exists("12345") == false, и бесшовное обновление молча
            // превращалось в обычный запуск.
            if (seamlessIdx + 1 < e.Args.Length && int.TryParse(e.Args[seamlessIdx + 1], out oldPid))
            {
                if (seamlessIdx + 2 < e.Args.Length)
                {
                    stateFilePath = e.Args[seamlessIdx + 2];
                }
            }
            else if (seamlessIdx + 1 < e.Args.Length)
            {
                // Формат без PID: сразу путь к файлу состояния.
                stateFilePath = e.Args[seamlessIdx + 1];
            }

            if (!string.IsNullOrEmpty(stateFilePath) && System.IO.File.Exists(stateFilePath))
            {
                var main = new MainWindow();
                main.Loaded += (_, _) => main.ApplyStateAndTakeover(stateFilePath, oldPid);
                main.Show();
                return;
            }
        }

        // Автоматический запуск с наивысшими правами Администратора (UAC Elevation)
        if (!Helpers.SuperAdminPrivilegeHelper.IsAdministrator() && !e.Args.Contains("--no-elevate"))
        {
            try
            {
                var proc = new System.Diagnostics.ProcessStartInfo
                {
                    UseShellExecute = true,
                    WorkingDirectory = Environment.CurrentDirectory,
                    FileName = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "Win11CopyDialog.exe",
                    Verb = "runas"
                };
                foreach (var arg in e.Args)
                {
                    proc.ArgumentList.Add(arg);
                }
                System.Diagnostics.Process.Start(proc);
                Shutdown();
                return;
            }
            catch
            {
                // Если пользователь отменил UAC диалог - продолжаем запуск
            }
        }

        // Активация всех системных привилегий токена Super-Admin (SeManageVolumePrivilege и др.)
        Helpers.SuperAdminPrivilegeHelper.EnableAllSuperAdminPrivileges();

        // Загрузка сохранённой конфигурации ДО применения темы.
        // Раньше AppConfigData только записывался на диск и никогда не читался,
        // поэтому ни одна настройка не переживала перезапуск приложения.
        string appConfigPath = Views.Dialogs.SettingsWindow.ConfigFilePath;

        var savedConfig = Views.Dialogs.AppConfigData.LoadFromFile(appConfigPath);
        Views.Dialogs.SettingsWindow.ApplyConfigToApp(savedConfig);

        // Явные аргументы командной строки имеют приоритет над сохранённым конфигом.
        if (e.Args.Contains("--dark"))
        {
            ThemeManager.Instance.Theme = AppTheme.MicaDark;
        }
        else if (e.Args.Contains("--light"))
        {
            ThemeManager.Instance.Theme = AppTheme.MicaLight;
        }

        // Применить тему до показа окон, чтобы Mica/тёмный режим встали сразу
        ThemeManager.Instance.Apply();

        if (e.Args.Contains("--replace-explorer"))
        {
            bool ok = Modules.WindowsShellIntegration.ShellIntegrationService.SetExplorerReplacement(true, out string err);
            if (ok)
            {
                MessageBox.Show("Motion Commander успешно назначен основным проводником Windows по умолчанию!", "Интеграция Shell", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show($"Ошибка назначения проводника: {err}", "Ошибка Shell", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            Shutdown(ok ? 0 : 1);
            return;
        }

        if (e.Args.Contains("--restore-explorer"))
        {
            bool ok = Modules.WindowsShellIntegration.ShellIntegrationService.SetExplorerReplacement(false, out string err);
            if (ok)
            {
                MessageBox.Show("Стандартный Windows Explorer успешно возвращен по умолчанию!", "Восстановление Shell", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show($"Ошибка восстановления проводника: {err}", "Ошибка Shell", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            Shutdown(ok ? 0 : 1);
            return;
        }

        // --theme-audit: прогон всех тем и проверка, что каждый ключ, на который
        // ссылается разметка, реально определён и перекрашивается.
        if (e.Args.Contains("--theme-audit"))
        {
            RunThemeAudit();
            Shutdown(0);
            return;
        }

        // --storage-audit: реальный прогон обнаружения накопителей, S.M.A.R.T.
        // и оценки состояния с выводом фактических значений.
        if (e.Args.Contains("--storage-audit"))
        {
            RunStorageAudit();
            Shutdown(0);
            return;
        }

        // --selftest: конструктор + классика + motion, прогнать 5 с, закрыться (exit 0).
        // Любая ошибка XAML/движка уронит процесс — это и есть проверка.
        if (e.Args.Contains("--selftest"))
        {
            new MainWindow().Show();
            var dlg = new CopyDialogWindow();
            dlg.StartSimulation(
                new[] { ("selftest_video.mp4", 800_000_000L), ("selftest_doc.pdf", 12_000_000L) },
                speedBytesPerSec: 300 * 1024 * 1024);
            dlg.SetDetails(true);
            dlg.Engine.Pause();
            dlg.Engine.Resume();
            dlg.Show();
            var motion = new MotionCopyWindow();
            motion.StartSimulation(
                new[] { ("selftest_photo.jpg", 9_000_000L), ("selftest_movie.mkv", 1_200_000_000L) },
                speedBytesPerSec: 300 * 1024 * 1024);
            motion.Show();
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            t.Tick += (_, _) => { t.Stop(); Shutdown(0); };
            t.Start();
        }

        if (e.Args.Contains("--bench-cli"))
        {
            // Раньше здесь был путь машины разработчика @"F:\ANTIGRAVITY\WIN11 COPY\bench_temp",
            // попадавший в релизные сборки. По умолчанию используем temp.
            string targetDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MotionCommanderBench");
            int idx = Array.IndexOf(e.Args, "--bench-cli");
            if (idx >= 0 && idx + 1 < e.Args.Length && !e.Args[idx + 1].StartsWith("--"))
            {
                targetDir = e.Args[idx + 1];
            }

            try
            {
                // Раньше здесь стояло .GetAwaiter().GetResult() — синхронное
                // блокирование на Dispatcher-потоке внутри Application.OnStartup.
                // Любой await внутри задачи перехватывал DispatcherSynchronizationContext
                // и взаимоблокировал UI на всё время многочасового бенчмарка.
                // Теперь бенчмарм выполняется в фоне, UI-поток не блокируется.
                var benchTask = Task.Run(() =>
                    Modules.PerformanceEngine.BenchmarkEngine.RunFullBenchmarkAsync(targetDir));
                benchTask.Wait();
                var report = benchTask.GetAwaiter().GetResult();

                string outJson = System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                string outPath = System.IO.Path.Combine(Helpers.AppPaths.BenchmarkDirectory, "benchmark_last_run.json");
                System.IO.File.WriteAllText(outPath, outJson);
            }
            catch (Exception ex)
            {
                string errPath = System.IO.Path.Combine(Helpers.AppPaths.BenchmarkDirectory, "benchmark_error.txt");
                System.IO.File.WriteAllText(errPath, ex.ToString());
            }
            finally
            {
                Shutdown(0);
            }
            return;
        }
        if (e.Args.Contains("--dark"))
        {
            ThemeManager.Instance.Theme = AppTheme.MicaDark;
            ThemeManager.Instance.Apply();
        }

        if (e.Args.Contains("--create-archive-demo"))
        {
            new Views.Dialogs.CreateArchiveWindow(new[] { @"C:\Windows\System32\notepad.exe" }).Show();
        }
        else if (e.Args.Contains("--extract-archive-demo"))
        {
            new Views.Dialogs.ExtractArchiveWindow(@"C:\Windows\explorer.exe").Show();
        }
        else if (e.Args.Contains("--wiztree"))
        {
            new Views.Dialogs.WizTreeAnalyzerWindow().Show();
        }
        else if (e.Args.Contains("--duplicates"))
        {
            new Views.Dialogs.DuplicateFinderWindow().Show();
        }
        else if (e.Args.Contains("--drivers"))
        {
            new Views.Dialogs.DriverInspectorWindow().Show();
        }
        else if (e.Args.Contains("--settings-window"))
        {
            new Views.Dialogs.SettingsWindow().Show();
        }
        else if (e.Args.Contains("--folder-picker-demo"))
        {
            // Раньше здесь был жёстко прописан путь машины разработчика
            // @"F:\ANTIGRAVITY\WIN11 COPY" — он попадал в релизные сборки
            // для всех пользователей. Используем переносимый путь.
            var picker = new Views.Dialogs.CyberFolderPickerDialog(
                "Выберите папку для копирования (Motion Transfer)",
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                42_500_000_000L);
            picker.Show();
        }
        else if (e.Args.Contains("--advanced-tools-demo"))
        {
            new Views.Dialogs.AdvancedToolsWindow(@"C:\Windows\System32\notepad.exe").Show();
        }
        else if (e.Args.Contains("--tab-transfer"))
        {
            var main = new MainWindow(null, 1);
            main.Show();
        }
        else if (e.Args.Contains("--tab-storage"))
        {
            var main = new MainWindow(null, 2);
            main.Show();
        }
        else if (e.Args.Contains("--tab-diagnostics"))
        {
            var main = new MainWindow(null, 3);
            main.Show();
        }
        else if (e.Args.Contains("--tab-tools"))
        {
            var main = new MainWindow(null, 4);
            main.Show();
        }
        else if (e.Args.Contains("--tab-settings-bottom"))
        {
            var main = new MainWindow(null, 5);
            main.Loaded += (_, _) => main.ScrollSettingsToBottom();
            main.Show();
        }
        else if (e.Args.Contains("--check-update-demo"))
        {
            var main = new MainWindow(null, 5);
            main.Loaded += async (_, _) =>
            {
                await Task.Delay(800);
                // Settings actions were moved or removed
            };
            main.Show();
        }
        else if (e.Args.Contains("--tab-settings"))
        {
            var main = new MainWindow(null, 5);
            main.Show();
        }
        else if (e.Args.Contains("--filemanager") || e.Args.Contains("--test-filemanager"))
        {
            var fm = new FileManagerWindow();
            fm.Show();
            if (e.Args.Contains("--test-filemanager"))
            {
                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                t.Tick += (_, _) => { t.Stop(); Shutdown(0); };
                t.Start();
            }
        }
        else if (e.Args.Contains("--motion-demo"))
        {
            // Сразу Motion Copy Engine с демо-набором
            var motion = new MotionCopyWindow();
            motion.StartSimulation(global::Win11CopyDialog.MainWindow.DefaultMixedScenario(), speedBytesPerSec: 150 * 1024 * 1024);
            motion.Show();
        }
        else
        {
            string? startDir = null;
            int initialTab = 0;
            if (e.Args.Contains("--tab-settings")) initialTab = 5;
            else if (e.Args.Contains("--tab-tools")) initialTab = 4;
            else if (e.Args.Contains("--tab-diagnostics")) initialTab = 3;
            else if (e.Args.Contains("--tab-storage") || e.Args.Contains("--storage")) initialTab = 2;
            else if (e.Args.Contains("--tab-transfer")) initialTab = 1;

            foreach (var arg in e.Args)
            {
                if (!arg.StartsWith("--") && (System.IO.Directory.Exists(arg) || System.IO.File.Exists(arg)))
                {
                    startDir = arg;
                    break;
                }
            }
            new MainWindow(startDir, initialTab).Show();
        }
    }

    /// <summary>
    /// Аудит тем: для каждой темы применяет её и проверяет, что все ключи,
    /// используемые в разметке, определены и заморожены.
    /// Именно этот прогон ловит регрессии вида «светлая тема рисует тёмные карточки».
    /// </summary>
    private void RunThemeAudit()
    {
        var res = Application.Current?.Resources;
        if (res == null) { System.Console.WriteLine("no resources"); return; }

        // Ключи, которые разметка обязана получать из палитры.
        string[] required =
        {
            "WindowBackgroundBrush","CardBackgroundBrush","CardBorderBrush",
            "GlassBorderBrush","SubtleBorderBrush","ChipBackgroundBrush",
            "LiquidGlassMaterialLevel0Brush","LiquidGlassMaterialLevel1Brush",
            "LiquidGlassMaterialLevel2Brush","LiquidGlassMaterialLevel3Brush",
            "LiquidGlassMaterialLevel4Brush","LiquidGlassMaterialLevel5Brush",
            "LiquidGlassMaterialLevel6Brush",
            "LiquidGlassBorderLevel1Brush","LiquidGlassBorderLevel2Brush",
            "LiquidGlassBorderLevel3Brush","LiquidGlassBorderLevel4Brush",
            "LiquidGlassBorderLevel5Brush","LiquidGlassSubtleBorderBrush",
            "LiquidGlassHoverBorderBrush","LiquidGlassCardBorderBrush",
            "LiquidGlassCardBackgroundBrush","LiquidGlassInputBackgroundBrush",
            "LiquidGlassInputBorderBrush","LiquidGlassInputHoverBackgroundBrush",
            "LiquidGlassInputFocusedBorderBrush","LiquidGlassHoverHighlight",
            "PrimaryTextBrush","TextPrimaryBrush","SecondaryTextBrush","TextSecondaryBrush",
            "LiquidGlassTextSecondaryBrush","TextTertiaryBrush","LiquidGlassTextTertiaryBrush",
            "MutedTextBrush","TextMutedBrush","TextDisabledBrush","TitleForegroundBrush",
            "StatusSuccessBrush","StatusWarningBrush","StatusDangerBrush",
            "SuccessGreenBrush","WarningAmberBrush","ErrorRedBrush",
            "AccentBrush","AccentHoverBrush","AccentPressedBrush","AccentForegroundBrush",
            "BaseFontFamily","BodyFontSize","CaptionFontSize","MinTouchTarget",
            "ScrollbarWidth","RadiusWindow","RadiusS","RadiusM","RadiusL","RadiusXL",
            "ScrollTrackBrush","ScrollThumbBrush","ScrollThumbHoverBrush",
            "HeaderBackgroundBrush","HeaderForegroundBrush","HeaderBorderBrush",
            "HeaderHoverBrush","NavDockBackgroundBrush","RibbonBackgroundBrush",
            "InputBackgroundBrush","ControlBackgroundBrush","HoverBrush","ListHoverBrush",
            "ProgressTrackBrush","GraphGridBrush","GraphFillBrush",
            "ContextMenuBackground","ContextMenuBorder","ContextMenuForeground",
            "ContextMenuHover","ContextMenuPressed","ContextMenuDisabled",
            "ContextMenuDanger","ContextMenuDivider",
        };

        // Поиск значения по всей цепочке словарей: собственный словарь приложения,
        // затем каждый объединённый (рекурсивно). Так же ведёт себя разрешение
        // {DynamicResource} в разметке, в отличие от голого res[key].
        static object? Lookup(ResourceDictionary dict, string key, int depth = 0)
        {
            if (depth > 4) return null;
            if (dict.Contains(key))
            {
                try { return dict[key]; } catch { return null; }
            }
            foreach (var merged in dict.MergedDictionaries)
            {
                if (merged == null) continue;
                var v = Lookup(merged, key, depth + 1);
                if (v != null) return v;
            }
            return null;
        }

        var problems = new List<string>();
        var tm = Models.ThemeManager.Instance;
        var originalTheme = tm.Theme;
        var originalAccent = tm.Accent;

        int themesChecked = 0;
        foreach (var theme in Models.ThemeManager.AllThemes)
        {
            foreach (var accent in new[] { null, tm.Accents[1] })
            {
                tm.Theme = theme;
                if (accent != null) tm.Accent = accent;
                themesChecked++;

                object?[] values = required.Select(k => Lookup(res, k)).ToArray();

                var missing = required
                    .Where((_, i) => values[i] == null)
                    .ToList();
                if (missing.Count > 0)
                    problems.Add($"{theme}/{tm.Accent.Name}: отсутствуют ключи: {string.Join(", ", missing)}");

                // Карточка должна быть не темнее окна в тёмной теме и не намного
                // светлее в светлой: иначе светлая тема рисует тёмные панели.
                int idxCard = Array.IndexOf(required, "CardBackgroundBrush");
                int idxWin = Array.IndexOf(required, "WindowBackgroundBrush");
                if (idxCard >= 0 && idxWin >= 0 &&
                    values[idxWin] is System.Windows.Media.SolidColorBrush wbg &&
                    values[idxCard] is System.Windows.Media.SolidColorBrush cbg)
                {
                    double cardLum = Lum(cbg.Color);
                    double winLum = Lum(wbg.Color);
                    if (tm.IsDark && cardLum + 0.02 < winLum)
                        problems.Add($"{theme}: карточка ({cardLum:F3}) темнее окна ({winLum:F3})");
                    if (!tm.IsDark && cardLum > winLum + 0.30)
                        problems.Add($"{theme}: карточка ({cardLum:F3}) намного светлее окна ({winLum:F3})");
                }

                // Все создаваемые кисти должны быть заморожены (иначе WPF клонирует их каждый кадр).
                var unfrozen = required
                    .Where((_, i) => values[i] is System.Windows.Freezable f && !f.IsFrozen)
                    .ToList();
                if (unfrozen.Count > 0)
                    problems.Add($"{theme}: незамороженные кисти: {string.Join(", ", unfrozen)}");
            }
        }

        tm.Theme = originalTheme;
        tm.Accent = originalAccent;

        System.Console.WriteLine($"THEME AUDIT: проверено комбинаций тем/акцентов: {themesChecked}");
        System.Console.WriteLine($"Тем всего: {Models.ThemeManager.AllThemes.Length}");
        if (problems.Count == 0)
        {
            System.Console.WriteLine("РЕЗУЛЬТАТ: OK - все ключи определены, карточки корректны, кисти заморожены");
        }
        else
        {
            System.Console.WriteLine($"РЕЗУЛЬТАТ: НАЙДЕНО ПРОБЛЕМ: {problems.Count}");
            foreach (var p in problems.Take(40)) System.Console.WriteLine("  " + p);
        }
        System.Console.Out.Flush();

        static double Lum(System.Windows.Media.Color c)
        {
            double F(byte v) { double s = v / 255.0; return s <= 0.04045 ? s / 12.92 : System.Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * F(c.R) + 0.7152 * F(c.G) + 0.0722 * F(c.B);
        }
    }

    /// <summary>
    /// Реальный прогон подсистемы накопителей: обнаружение, S.M.A.R.T., оценка.
    /// Печатает только фактические значения и честно отмечает недоступные,
    /// чтобы было видеть, откуда взялась каждая цифра в интерфейсе.
    /// </summary>
    private void RunStorageAudit()
    {
        System.Console.WriteLine("STORAGE AUDIT — только реальные измерения");
        System.Console.WriteLine(new string('=', 72));
        System.Console.Out.Flush();

        List<Modules.StorageControlCenter.Models.StorageDisk> disks;
        try
        {
            System.Console.WriteLine("  [шаг 1] запрашиваю список накопителей через WMI...");
            System.Console.Out.Flush();
            var sw0 = System.Diagnostics.Stopwatch.StartNew();
            Modules.StorageControlCenter.Services.StorageDiscoveryService.TraceEnabled = true;
            disks = Modules.StorageControlCenter.Services.StorageDiscoveryService.GetAllDisks(forceRefresh: true);
            System.Console.WriteLine($"  [шаг 1] готово за {sw0.ElapsedMilliseconds} мс, найдено {disks.Count}");
            System.Console.Out.Flush();
        }
        catch (Exception ex)
        {
            System.Console.WriteLine("ОБНАРУЖЕНИЕ НЕ УДАЛОСЬ: " + ex.Message);
            System.Console.Out.Flush();
            return;
        }

        System.Console.WriteLine($"Найдено накопителей: {disks.Count}");
        System.Console.WriteLine("");

        foreach (var disk in disks)
        {
            try { Modules.StorageControlCenter.Services.SmartHealthService.EnrichDiskHealth(disk); }
            catch (Exception ex) { System.Console.WriteLine($"  S.M.A.R.T. сбой: {ex.Message}"); }
            try { Modules.StorageControlCenter.Services.StorageAdvisorService.EvaluateScore(disk); }
            catch (Exception ex) { System.Console.WriteLine($"  Оценка сбой: {ex.Message}"); }

            System.Console.WriteLine($"--- #{disk.DiskNumber} ---");
            System.Console.WriteLine($"  Модель        : {disk.Model}");
            System.Console.WriteLine($"  Серийный номер: {Or(disk.SerialNumber)}");
            System.Console.WriteLine($"  Тип носителя  : {disk.MediaType}");
            System.Console.WriteLine($"  Шина          : {disk.BusType}");
            System.Console.WriteLine($"  Ёмкость       : {disk.TotalSizeFormatted}");
            System.Console.WriteLine($"  Занято        : {disk.UsedSpacePercent:F1}%");
            System.Console.WriteLine($"  Свободно      : {disk.FreeSpaceFormatted}");
            System.Console.WriteLine($"  HealthStatus  : {Or(disk.HealthStatus)}");

            // Данные показываются только когда реально измерены.
            System.Console.WriteLine($"  Температура   : {(disk.HasTemperature ? disk.TemperatureFormatted + "  [" + disk.Source + "]" : "НЕТ ДАННЫХ - " + Or(disk.TelemetryNote))}");
            System.Console.WriteLine($"  Износ         : {disk.WearFormatted}");
            System.Console.WriteLine($"  Наработка     : {disk.PowerOnHoursFormatted}");
            System.Console.WriteLine($"  Циклы вкл.    : {disk.PowerCyclesFormatted}");
            System.Console.WriteLine($"  Записано      : {disk.TotalWrittenFormatted}");

            System.Console.WriteLine($"  ОЦЕНКА        : {disk.Score.Grade}  " +
                                     (disk.Score.IsCalculated ? $"{disk.Score.TotalScore:F1} / 100" : "НЕ ВЫСТАВЛЕНА"));
            System.Console.WriteLine($"  Статус        : {disk.Score.StatusText}");
            System.Console.WriteLine($"  Основа        : {disk.Score.BasisDescription} ({disk.Score.MeasuredComponentCount}/4 показателя)");
            System.Console.WriteLine($"  Заполненность : {disk.Score.SpaceScore}/100 (справочно, в оценку не входит)");

            System.Console.WriteLine($"  Разделов      : {disk.Partitions.Count}");
            foreach (var p in disk.Partitions)
                System.Console.WriteLine($"     {Or(p.DriveLetter),-3} {Or(p.FileSystem),-8} {Or(p.VolumeLabel),-20} {p.SizeFormatted}");

            System.Console.WriteLine($"  S.M.A.R.T. атрибутов: {disk.SmartAttributes.Count}" +
                (disk.HasSmartAttributes ? "" : "  (контроллер не публикует предиктивные данные)"));
            foreach (var a in disk.SmartAttributes.Take(6))
                System.Console.WriteLine($"     0x{a.Id:X2} {Or(a.Name),-24} raw={a.RawValue,-10} {a.Status}");

            if (disk.Score.Warnings.Count > 0)
                foreach (var w in disk.Score.Warnings)
                    System.Console.WriteLine($"  ПРЕДУПРЕЖДЕНИЕ: {w}");

            System.Console.WriteLine("");
        }

        // Итог: сколько дисков реально имеют данные состояния.
        int withHealth = disks.Count(x => x.Score.MeasuredComponentCount > 0);
        System.Console.WriteLine(new string('=', 72));
        System.Console.WriteLine($"Дисков с реальными данными состояния: {withHealth} из {disks.Count}");
        System.Console.WriteLine(withHealth == 0
            ? "Вывод: контроллеры не отдают S.M.A.R.T. и температуру. Интерфейс обязан"
              + " показывать «н/д», а не подставлять значения. Если выше видны A+ или °C — это ошибка."
            : "Вывод: часть показателей действительно измерена.");
        System.Console.Out.Flush();

        static string Or(string? s) => string.IsNullOrWhiteSpace(s) ? "—" : s.Trim();
    }

    private void CleanupOldFiles()
    {
        try
        {
            string currentDir = AppDomain.CurrentDomain.BaseDirectory;
            var oldFiles = System.IO.Directory.GetFiles(currentDir, "*.old", System.IO.SearchOption.TopDirectoryOnly);
            foreach (var file in oldFiles)
            {
                try { System.IO.File.Delete(file); } catch { }
            }
            // staging теперь живёт в профиле пользователя, а не рядом с exe.
            string stagingDir = Helpers.AppPaths.StagingDirectory;
            if (System.IO.Directory.Exists(stagingDir))
            {
                try { System.IO.Directory.Delete(stagingDir, true); } catch { }
            }
        }
        catch { }
    }
}

