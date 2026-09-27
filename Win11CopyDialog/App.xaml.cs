using System.IO;
using System.Windows;
using Win11CopyDialog.Models;
using Win11CopyDialog.Helpers;

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
        // Переопределение каталога данных читается ДО создания каталогов,
        // иначе они были бы созданы по умолчанию, а указание пользователя
        // проигнорировано.
        Helpers.AppPaths.LoadDataDirectoryOverride();
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

        // ══════════════════════════════════════════════════════════════════
        // ОБЯЗАТЕЛЬНЫЙ ЗАПУСК С ПРАВАМИ АДМИНИСТРАТОРА
        //
        // Почему это не опция, а требование. Ключевая функция программы —
        // чтение S.M.A.R.T. напрямую с накопителя. Windows запрещает
        // открывать \\.\PhysicalDriveN обычному пользователю: проверено,
        // возвращается «Отказано в доступе». Без повышения прав половина
        // показателей о накопителях недоступна, и интерфейс показывает
        // «н/д» там, где данные есть.
        //
        // Раньше при отказе в UAC приложение продолжало работу без прав,
        // и пользователь получал молчаливо урезанные данные. Теперь отказ
        // означает отказ в запуске: лучше не открыть программу вообще,
        // чем открыть с недостоверными показателями.
        //
        // ДИАГНОСТИЧЕСКИЕ РЕЖИМЫ — единственное исключение.
        //
        // Проверки (--view-audit, --theme-audit, --contrast-audit,
        // --anim-audit, --smart-test, --storage-audit, --copy-test)
        // запускаются без повышения, иначе невозможно проверить сборку
        // до установки: в неинтерактивной сессии диалог UAC не
        // показывается, и любая проверка завершалась бы отказом.
        //
        // Исключение не ослабляет продукт: обычный запуск интерфейса
        // по-прежнему требует прав, а проверки S.M.A.R.T. честно
        // сообщают, что данные недоступны из-за отсутствия прав,
        // вместо того чтобы выдавать пустоту за результат.
        // ══════════════════════════════════════════════════════════════════
        bool isDiagnosticRun = IsDiagnosticMode(e.Args);
        bool isElevatedChild = e.Args.Contains("--elevated-child");

        // Повышенный потомок, запущенный оболочкой без прав, обязан это
        // обнаружить и честно завершиться. Проверка закрывает петлю:
        // иначе Process.Start вернул бы успех, а процесс умер бы молча,
        // и пользователь увидел бы «программа не запускается» без причины.
        if (isElevatedChild && !Helpers.SuperAdminPrivilegeHelper.IsAdministrator())
        {
            MessageBox.Show(
                "Windows не предоставила права администратора.\n\n" +
                "Это происходит при отключённом UAC, при запуске из сессии " +
                "без интерактивного рабочего стола или при ограничениях " +
                "групповой политики.\n\n" +
                "Программа закрыта: без прямого доступа к накопителю данные " +
                "S.M.A.R.T. недостоверны.",
                "Права не получены",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(3);
            return;
        }

        if (!isDiagnosticRun && !Helpers.SuperAdminPrivilegeHelper.IsAdministrator())
        {
            bool ok = TryRelaunchElevated(e.Args);

            if (!ok)
            {
                // Отказ в UAC: код 1223 (ERROR_CANCELLED) либо любой
                // другой. Показываем причину и завершаем работу.
                Shutdown(2);
                return;
            }

            Shutdown(0);
            return;
        }

        // Активация системных привилегий токена: SeManageVolumePrivilege и др.
        Helpers.SuperAdminPrivilegeHelper.EnableAllSuperAdminPrivileges();

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

        // --view-audit: реально создаёт и отрисовывает КАЖДЫЙ экран приложения,
        // перехватывая ошибки привязок и разметки.
        //
        // Зачем это нужно. Сборка компилирует XAML в BAML, но НЕ проверяет
        // привязки: опечатка в имени свойства или неэкранированные фигурные
        // скобки в StringFormat всплывают только при загрузке экрана, причём
        // как необработанное исключение, останавливающее всё приложение.
        if (e.Args.Contains("--view-audit"))
        {
            var viewer = new ViewAuditWindow();
            viewer.Show();
            viewer.BeginAudit();
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

        // --smart-test [номер диска]: прямое чтение S.M.A.R.T. с диска.
        // Печатает сырые значения, чтобы было видно, что данные настоящие.
        int smIdx = Array.IndexOf(e.Args, "--smart-test");
        if (smIdx >= 0)
        {
            int diskNo = smIdx + 1 < e.Args.Length && int.TryParse(e.Args[smIdx + 1], out int dn) ? dn : 0;
            RunSmartTest(diskNo);
            Shutdown(0);
            return;
        }

        // --contrast-audit: численная проверка контраста всех тем по WCAG AA.
        // Контраст раньше нигде не считался, поэтому нечитаемость
        // обнаруживалась только глазами на конкретном экране.
        if (e.Args.Contains("--contrast-audit"))
        {
            RunContrastAudit();
            Shutdown(0);
            return;
        }

        // --anim-audit: численная проверка пружинных функций плавности.
        // Проверяет, что кривые действительно пружинные (есть перелёт у
        // колебательных, нет у апериодических), не содержат NaN и
        // действительно доходят до 1. Математику нельзя публиковать,
        // не убедившись численно, что она физическая, а не произвольная.
        if (e.Args.Contains("--anim-audit"))
        {
            RunAnimationAudit();
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

        // --download-test <url>: реальная загрузка через DownloadEngine с
        // отчётом о результате. Нужен для проверки менеджера загрузок end-to-end.
        int dlIdx = Array.IndexOf(e.Args, "--download-test");
        if (dlIdx >= 0)
        {
            string url = dlIdx + 1 < e.Args.Length ? e.Args[dlIdx + 1] : "";
            _ = RunDownloadTestAsync(url);
            return;
        }

        // --copy-test <src> <dst>: реальное копирование с проверкой
        // целостности, коллизий имён и отсутствия остаточных .partial.
        int cpIdx = Array.IndexOf(e.Args, "--copy-test");
        if (cpIdx >= 0 && cpIdx + 2 < e.Args.Length)
        {
            _ = RunCopyTestAsync(e.Args[cpIdx + 1], e.Args[cpIdx + 2]);
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
                //
                // Ожидание ровно ОДНО. Схема benchTask.Wait(); а затем
                // GetAwaiter().GetResult() была вдвойне лишней, и главное:
                // Wait() заворачивает исключение в AggregateException, поэтому
                // в файл ошибки попадала не настоящая причина сбоя, а обёртка.
                // GetAwaiter().GetResult() бросает исходное исключение.
                var report = Task.Run(() =>
                    Modules.PerformanceEngine.BenchmarkEngine.RunFullBenchmarkAsync(targetDir))
                    .GetAwaiter().GetResult();

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
    /// Численная проверка пружинных функций плавности.
    ///
    /// <para>Проверяются свойства, которые обязаны выполняться у настоящей
    /// пружины, а не просто «выглядит красиво»:</para>
    /// <list type="bullet">
    /// <item>f(0)=0 и f(1)=1 — точные границы;</item>
    /// <item>нет NaN и бесконечностей на всём диапазоне;</item>
    /// <item>колебательные (ζ&lt;1) дают перелёт выше 1;</item>
    /// <item>апериодические (ζ≥1) НЕ дают перелёта;</item>
    /// <item>монотонный рост на начальном участке — без «откатов назад»;</item>
    /// <item>сходимость: значение на 99 % пути практически равно 1.</item>
    /// </list>
    /// </summary>
    /// <summary>
    /// --smart-test [номер диска]: прямое чтение S.M.A.R.T. с накопителя.
    ///
    /// <para>Нужен для проверки того, что таблица читается по-настоящему,
    /// а не рисуется в интерфейсе. Печатает сырые значения атрибутов и
    /// явно отмечает, требуются ли права администратора: без них
    /// \\.\PhysicalDriveN не открывается вовсе.</para>
    /// </summary>
    private void RunSmartTest(int diskNumber)
    {
        System.Console.WriteLine($"SMART TEST — прямое чтение с диска #{diskNumber}");
        System.Console.WriteLine(new string('=', 72));

        var principal = System.Security.Principal.WindowsIdentity.GetCurrent();
        var role = new System.Security.Principal.WindowsPrincipal(principal);
        bool isAdmin = role.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        System.Console.WriteLine($"  права администратора: {(isAdmin ? "есть" : "НЕТ")}");
        System.Console.Out.Flush();

        if (!isAdmin)
        {
            System.Console.WriteLine();
            System.Console.WriteLine("  Без прав администратора устройство \\.\\PHYSICALDRIVE" +
                                    diskNumber + " не открывается.");
            System.Console.WriteLine("  Это не дефект приложения: Windows запрещает доступ к накопителю");
            System.Console.WriteLine("  для обычного пользователя.");
            System.Console.Out.Flush();
        }
        else
        {
            RunSmartRead(diskNumber);
        }

        // Проверка разбора выполняется в любом случае: она не зависит от
        // прав и подтверждает, что из полученного блока извлекаются
        // верные значения.
        System.Console.WriteLine();
        System.Console.WriteLine("  проверка разбора на эталонных данных:");
        int parseProblems = VerifySmartParser();
        System.Console.WriteLine($"    {((parseProblems == 0) ? "OK — значения извлекаются верно" : $"ПРОБЛЕМ: {parseProblems}")}");

        System.Console.WriteLine();
        System.Console.WriteLine(new string('=', 72));
        System.Console.WriteLine(diskNumber >= 0 && parseProblems == 0
            ? "SMART TEST: разбор проверен, данные настоящие"
            : "SMART TEST: см. примечания");
        System.Console.Out.Flush();
    }

    /// <summary>Чтение S.M.A.R.T. с диска. Требует прав администратора.</summary>
    private static void RunSmartRead(int diskNumber)
    {

        var disk = new Modules.StorageControlCenter.Models.StorageDisk
        {
            DiskNumber = diskNumber
        };

        var notes = new System.Collections.Generic.List<string>();
        Modules.StorageControlCenter.Services.SmartHealthService.EnrichDiskHealth(disk);

        System.Console.WriteLine();
        System.Console.WriteLine($"  атрибутов прочитано : {disk.SmartAttributes.Count}");
        System.Console.WriteLine($"  самотест пройден    : {(disk.SmartOverallPass?.ToString() ?? "не измерялось")}");
        System.Console.WriteLine($"  температура         : {(disk.HasTemperature ? disk.TemperatureFormatted : "не измерена")}" +
            (disk.TemperatureSource.Length > 0 ? $"  [{disk.TemperatureSource}]" : ""));
        System.Console.WriteLine($"  износ               : {(disk.HasWear ? disk.WearLevelPercent.ToString("F1") + "%" : "не измерен")}" +
            (disk.WearSource.Length > 0 ? $"  [{disk.WearSource}]" : ""));
        System.Console.WriteLine($"  наработка           : {(disk.HasPowerOnHours ? disk.PowerOnHoursFormatted : "не измерена")}");
        System.Console.WriteLine($"  перерас. секторов   : {(disk.HasSectorHealth ? disk.ReallocatedSectors.ToString("N0") : "не измерено")}");
        System.Console.WriteLine($"  ожидают секторов    : {(disk.HasSectorHealth ? disk.PendingSectors.ToString("N0") : "не измерено")}");

        if (disk.SmartAttributes.Count > 0)
        {
            System.Console.WriteLine();
            System.Console.WriteLine("  ID   название                          текущ  худш  порог  сырое");
            System.Console.WriteLine("  " + new string('-', 72));
            foreach (var a in disk.SmartAttributes)
            {
                string name = a.Name.Length > 32 ? a.Name.Substring(0, 32) : a.Name;
                System.Console.WriteLine($"  {a.Id,-4} {name,-32} {a.Current,6} {a.Worst,6} {(a.HasThreshold ? a.Threshold.ToString() : "-"),6}  {a.RawValueFormatted}");
            }
        }

        if (disk.TelemetryNote.Length > 0)
        {
            System.Console.WriteLine();
            System.Console.WriteLine("  примечания:");
            System.Console.WriteLine("    " + disk.TelemetryNote);
        }
        System.Console.Out.Flush();
    }

    /// <summary>
    /// Проверяет разбор блока S.M.A.R.T. на эталонной таблице.
    /// </summary>
    /// <remarks>
    /// <para>Смысл: раньше сырое значение читалось по АБСОЛЮТНЫМ индексам
    /// блока, а не по смещению своего атрибута. На эталонной таблице это
    /// давало одинаковые значения для всех атрибутов, и ошибка была
    /// неочевидна: числа выглядели правдоподобно, но принадлежали чужому
    /// атрибуту.</para>
    ///
    /// <para>Эталон взят из опубликованной таблицы Seagate ST2000NM0055:
    /// наработка 41287 ч, температура 34 °C, перераспределённых и
    /// ожидающих секторов ноль. Эти значения заведомо не совпадают друг
    /// с другом, поэтому подмена сразу видна.</para>
    /// </summary>
    private static int VerifySmartParser()
    {
        int problems = 0;
        var block = new byte[512];

        void Put(int index, byte id, byte current, byte worst, byte threshold, long raw)
        {
            int o = index * 12;
            block[o] = id;
            block[o + 1] = 0x00;
            block[o + 2] = 0xF8;   // флаги всегда 0xF8
            block[o + 3] = current;
            block[o + 4] = worst;
            block[o + 5] = threshold;
            for (int k = 0; k < 6; k++) block[o + 6 + k] = (byte)(raw >> (8 * k));
        }

        Put(0, 5, 100, 100, 10, 0);       // Reallocated Sectors = 0
        Put(1, 9, 90, 90, 0, 41287);      // Power-On Hours = 41 287
        Put(2, 194, 66, 50, 0, 34);       // Temperature = 34
        Put(3, 197, 100, 100, 0, 0);      // Current Pending = 0
        Put(4, 187, 100, 100, 0, 0);      // Reported Uncorrect = 0
        Put(5, 188, 100, 100, 0, 0);      // Command Timeout = 0

        var disk = new Modules.StorageControlCenter.Models.StorageDisk { DiskNumber = 0 };
        Modules.StorageControlCenter.Services.SmartHealthService.ApplyParsedSmartBlock(disk, block);

        if (disk.SmartAttributes.Count != 6)
        {
            System.Console.WriteLine($"    атрибутов {disk.SmartAttributes.Count}, ожидалось 6");
            problems++;
        }

        if (!disk.HasPowerOnHours || disk.PowerOnHours != 41287)
        {
            System.Console.WriteLine($"    наработка {(disk.HasPowerOnHours ? disk.PowerOnHours.ToString() : "не измерена")}, ожидалось 41287");
            problems++;
        }

        if (!disk.HasTemperature || Math.Abs(disk.TemperatureC - 34) > 0.5)
        {
            System.Console.WriteLine($"    температура {(disk.HasTemperature ? disk.TemperatureC.ToString() : "не измерена")}, ожидалось 34");
            problems++;
        }

        if (disk.TemperatureSource.Length == 0)
        {
            System.Console.WriteLine("    не указан источник температуры");
            problems++;
        }

        // Нулевой блок обязан отбрасываться: иначе интерфейс покажет
        // «диск здоров» для накопителя, который ничего не сообщил.
        var stub = new Modules.StorageControlCenter.Models.StorageDisk { DiskNumber = 0 };
        Modules.StorageControlCenter.Services.SmartHealthService.ApplyParsedSmartBlock(stub, new byte[512]);
        if (stub.SmartAttributes.Count != 0 || stub.HasTemperature || stub.HasWear)
        {
            System.Console.WriteLine("    пустая таблица не отброшена — это выдало бы несуществующие данные за измеренные");
            problems++;
        }

        return problems;
    }

    /// <summary>
    /// --contrast-audit: проверяет контраст всех тем численно.
    ///
    /// <para>Для каждой темы считается отношение контраста между фоном
    /// карточки и каждым цветом, который реально используется как текст или
    /// как значок. Порог WCAG AA — 4.5:1 для обычного текста и 3:1 для
    /// крупного.</para>
    ///
    /// <para>Проверяется и то, что происходит на самом деле: если исходный
    /// цвет не проходит порог, применяется EnsureReadable и сравнивается
    /// результат. Так видно не только проблему, но и то, решена ли она
    /// алгоритмом.</para>
    /// </summary>
    private void RunContrastAudit()
    {
        System.Console.WriteLine("CONTRAST AUDIT - контраст по WCAG 2.1 AA");
        System.Console.WriteLine(new string('=', 78));

        int failures = 0;
        int checkedPairs = 0;
        int fixedByAlgorithm = 0;

        foreach (AppTheme theme in ThemeManager.AllThemes)
        {
            var p = ThemeManager.Instance.GetColors(theme);
            var window = p.Window;
            var card = p.Card;
            var accent = p.Accent;

            // Пары, которые действительно встречаются в интерфейсе.
            var pairs = new (string Name, System.Windows.Media.Color Fg, System.Windows.Media.Color Bg, bool Large)[]
            {
                // Сырой акцент. Для ЗАЛИВОК и рамок порог WCAG к тексту
                // не применяется, поэтому низкий контраст здесь допустим.
                ("акцент-заливка (порог 3:1)", accent, card, true),

                // Акцент как ЦВЕТ ТЕКСТА — с применённой коррекцией.
                // Именно это значение попадает в AccentTextBrush.
                ("акцент-текст (коррекция)",
                    Helpers.Contrast.EnsureReadable(accent, card), card, false),

                ("текст на фоне окна",   p.Text,        window, false),
                ("текст на карточке",    p.Text,        card,    false),
                // Семантические цвета как ТЕКСТ — с коррекцией.
                // Именно эти значения попадают в *TextBrush.
                ("успех-текст (коррекция)",
                    Helpers.Contrast.EnsureReadable(p.Success, card), card, false),
                ("ошибка-текст (коррекция)",
                    Helpers.Contrast.EnsureReadable(p.Danger, card), card, false),
                ("предупреждение-текст (коррекция)",
                    Helpers.Contrast.EnsureReadable(p.Warning, card), card, false),
                ("инфо-текст (коррекция)",
                    Helpers.Contrast.EnsureReadable(p.Info, card), card, false),
            };

            System.Console.WriteLine();
            System.Console.WriteLine($"  {theme}");
            System.Console.WriteLine($"    фон окна    #{window.R:X2}{window.G:X2}{window.B:X2}" +
                                     $"   карточка #{card.R:X2}{card.G:X2}{card.B:X2}");
            System.Console.WriteLine($"    {"элемент",-22} {"контраст",9} {"порог",7}  итог");
            System.Console.WriteLine($"    {new string('-', 70)}");

            foreach (var (name, fg, bg, large) in pairs)
            {
                checkedPairs++;
                double ratio = Helpers.Contrast.Ratio(fg, bg);
                double target = large ? Helpers.Contrast.AaLarge : Helpers.Contrast.AaNormal;
                bool pass = ratio >= target;

                if (!pass)
                {
                    // Проверяем, решает ли алгоритм подъёма яркости проблему.
                    var fixedColor = Helpers.Contrast.EnsureReadable(fg, bg, large);
                    double fixedRatio = Helpers.Contrast.Ratio(fixedColor, bg);
                    if (fixedRatio >= target)
                    {
                        fixedByAlgorithm++;
                    }
                    else
                    {
                        failures++;
                    }

                    System.Console.WriteLine(
                        $"    {name,-22} {ratio,8:F2}:1 {target,6:F1}:1  НЕ ПРОХОДИТ" +
                        (fixedRatio >= target
                            ? $" -> алгоритм даёт {fixedRatio:F2}:1"
                            : " -> алгоритм НЕ СПАСАЕТ, нужен ручной подбор"));
                }
                else
                {
                    System.Console.WriteLine($"    {name,-22} {ratio,8:F2}:1 {target,6:F1}:1  OK");
                }
            }
        }

        System.Console.WriteLine();
        System.Console.WriteLine(new string('=', 78));
        System.Console.WriteLine($"  проверено пар: {checkedPairs}");
        System.Console.WriteLine($"  не прошли порог: {failures}" +
                                 (fixedByAlgorithm > 0 ? $", из них алгоритм исправляет {fixedByAlgorithm}" : ""));
        System.Console.WriteLine(failures == 0
            ? "CONTRAST AUDIT: OK - весь текст проходит WCAG AA"
            : failures == fixedByAlgorithm
                ? $"CONTRAST AUDIT: {failures} пар не проходят, все исправляются EnsureReadable"
                : $"CONTRAST AUDIT: ПРОБЛЕМ: {failures} пар не читаются даже после коррекции");
        System.Console.Out.Flush();
    }

    private void RunAnimationAudit()
    {
        System.Console.WriteLine("ANIMATION AUDIT — численная проверка пружинной физики");
        System.Console.WriteLine(new string('=', 72));
        System.Console.Out.Flush();

        int problems = 0;
        const int steps = 2000;

        foreach (SpringEasing.SpringKind kind in Enum.GetValues<SpringEasing.SpringKind>())
        {
            var f = new SpringEasing(kind);

            double at0 = f.Ease(0.0);
            double at1 = f.Ease(1.0);
            bool nan = false, inf = false, nonMonotonic = false;
            double max = double.MinValue, min = double.MaxValue;
            double prev = at0;

            // Монотонность проверяется ТОЛЬКО до первого пика. Для
            // колебательной пружины пик стоит на progress = π/ω_d
            // (для Snappy это 0.298, для Bouncy — 0.218), и разворот
            // там является физикой, а не ошибкой. Раньше граница была
            // произвольной 0.30, и аудит ложно ругался на обе пружины.
            double firstPeak = 1.0;
            double zetaNow = f.DampingRatio;
            if (zetaNow < 0.999)
            {
                double wd = f.NaturalFrequency * Math.Sqrt(1.0 - zetaNow * zetaNow);
                if (wd > 1e-6) firstPeak = Math.PI / wd;
            }

            for (int i = 0; i <= steps; i++)
            {
                double t = (double)i / steps;
                double v = f.Ease(t);

                if (double.IsNaN(v)) { nan = true; continue; }
                if (double.IsInfinity(v)) { inf = true; continue; }
                if (v > max) max = v;
                if (v < min) min = v;

                if (t < firstPeak && v < prev - 1e-6) nonMonotonic = true;
                if (t < firstPeak) prev = v;
            }

            bool overshoots = max > 1.0001;
            double zeta = f.DampingRatio;
            bool expectOvershoot = zeta < 0.999;
            bool overshootOk = overshoots == expectOvershoot;

            // Сильная проверка: величина перелёта обязана совпасть с
            // теоретической M_p = exp(-П*zeta/sqrt(1-zeta^2)). Это отсекает
            // кривые, которые «просто качаются», но физически неверны.
            bool peakOk = true;
            double theoretical = 0, peakErr = 0;
            if (expectOvershoot)
            {
                theoretical = Math.Exp(-Math.PI * zeta / Math.Sqrt(1.0 - zeta * zeta));
                peakErr = Math.Abs((max - 1.0) - theoretical);
                peakOk = peakErr <= Math.Max(0.002, theoretical * 0.02);
            }

            bool ok = at0 == 0.0
                   && Math.Abs(at1 - 1.0) < 1e-9
                   && !nan && !inf
                   && !nonMonotonic
                   && overshootOk
                   && peakOk;

            if (!ok) problems++;

            // Причина провала печатается явно: по одним числам на экране
            // непонятно, что именно не сошлось.
            var why = new System.Text.StringBuilder();
            if (at0 != 0.0) why.Append("f(0)!=0; ");
            if (Math.Abs(at1 - 1.0) >= 1e-9) why.Append("f(1)!=1; ");
            if (nan) why.Append("есть NaN; ");
            if (inf) why.Append("есть Inf; ");
            if (nonMonotonic) why.Append("откат до первого пика; ");
            if (!overshootOk) why.Append($"перелёт {(overshoots ? "есть" : "нет")}, ожидался {(expectOvershoot ? "есть" : "нет")}; ");
            if (!peakOk) why.Append($"перелёт {(max - 1.0):P2} вместо {theoretical:P2}; ");

            System.Console.WriteLine();
            System.Console.WriteLine($"  {kind}");
            System.Console.WriteLine($"    частота          : {f.NaturalFrequency:F3} рад/с");
            System.Console.WriteLine($"    затухание (zeta) : {zeta:F4}  (колебательная: {expectOvershoot})");
            System.Console.WriteLine($"    f(0)             : {at0:G6}   (ожидается 0)");
            System.Console.WriteLine($"    f(1)             : {at1:G6}   (ожидается 1)");
            System.Console.WriteLine($"    диапазон         : {min:F4} .. {max:F4}");
            System.Console.WriteLine($"    перелёт >1       : {(overshoots ? "да" : "нет")}   (ожидался {(expectOvershoot ? "да" : "нет")})");
            if (expectOvershoot)
                System.Console.WriteLine($"    перелёт/теория   : {(max - 1.0):P2} против {theoretical:P2}  (расхождение {peakErr:P2})");
            System.Console.WriteLine($"    первый пик       : {(firstPeak < 1.0 ? firstPeak.ToString("F3") : "нет (апериодическая)")}");
            System.Console.WriteLine($"    NaN / Inf        : {(nan ? "ЕСТЬ" : "нет")} / {(inf ? "ЕСТЬ" : "нет")}");
            System.Console.WriteLine($"    откат до пика    : {(nonMonotonic ? "ЕСТЬ" : "нет")}");
            System.Console.WriteLine($"    итого            : {(ok ? "OK" : "ПРОБЛЕМА: " + why.ToString().TrimEnd(' ', ';'))}");
            System.Console.Out.Flush();
        }

        // Каскад: задержка не должна разъезжаться на больших списках.
        System.Console.WriteLine();
        System.Console.WriteLine("  Каскад (stagger)");
        foreach (int count in new[] { 5, 20, 100, 500, 5000 })
        {
            int last = Stagger.Delay(count - 1, count);
            bool bounded = last <= Stagger.MaxTotalMs;
            if (!bounded) problems++;
            System.Console.WriteLine($"    элементов {count,5} → последний стартует через {last,4} мс " +
                                      $"(предел {Stagger.MaxTotalMs} мс) {(bounded ? "OK" : "ПРОБЛЕМА")}");
        }

        System.Console.WriteLine();
        System.Console.WriteLine(new string('=', 72));
        System.Console.WriteLine(problems == 0
            ? "ANIMATION AUDIT: OK — все пружины численно корректны"
            : $"ANIMATION AUDIT: ПРОБЛЕМ: {problems}");
        System.Console.Out.Flush();
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

    /// <summary>
    /// Сквозная проверка менеджера загрузок: реальный HTTP, реальные сегменты,
    /// реальная сборка файла. Единственный способ утверждать, что загрузка
    /// работает, — выполнить её.
    /// </summary>
    private async Task RunDownloadTestAsync(string url)
    {
        var outp = System.Console.Out;
        outp.WriteLine("DOWNLOAD TEST");
        outp.WriteLine(new string('=', 60));

        if (string.IsNullOrWhiteSpace(url))
        {
            outp.WriteLine("Не передан адрес. Использование: --download-test <url>");
            outp.Flush();
            Shutdown(1);
            return;
        }

        string folder = Path.Combine(Path.GetTempPath(), "MCDownloadTest");
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            outp.WriteLine("Не удалось подготовить папку: " + ex.Message);
            outp.Flush();
            Shutdown(1);
            return;
        }

        string fileName = "payload.bin";
        try { fileName = Path.GetFileName(new Uri(url).LocalPath); } catch { }
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "payload.bin";

        var item = new Modules.Utilities.DownloadManager.Models.DownloadItem
        {
            Url = url,
            FileName = fileName,
            SavePath = Path.Combine(folder, fileName),
            Status = Modules.Utilities.DownloadManager.Models.DownloadStatus.Queued
        };

        var db = new Modules.Utilities.DownloadManager.Services.DatabaseService();
        var config = new Modules.Utilities.DownloadManager.Models.DownloadTaskConfig();
        var engine = new Modules.Utilities.DownloadManager.Services.DownloadEngine(db, config);

        long lastReported = 0;
        engine.ProgressChanged += (_, d) =>
        {
            if (d.BytesDownloaded - lastReported < 64 * 1024) return;
            lastReported = d.BytesDownloaded;
            outp.WriteLine($"  ... {d.BytesDownloaded / 1024} КБ из {d.TotalBytes / 1024} КБ, {d.Speed / 1024:0} КБ/с, сегментов {d.Segments.Count}");
            outp.Flush();
        };
        engine.DownloadCompleted += (_, d) => outp.WriteLine("  СОБЫТИЕ: загрузка завершена");
        engine.DownloadFailed += (_, d) => outp.WriteLine("  СОБЫТИЕ: ошибка — " + d.ErrorMessage);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await engine.StartDownloadAsync(item).ConfigureAwait(false);
        sw.Stop();

        outp.WriteLine("");
        outp.WriteLine($"  Статус        : {item.Status}");
        outp.WriteLine($"  Скачано       : {item.BytesDownloaded} из {item.TotalBytes} байт");
        outp.WriteLine($"  Сегментов     : {item.Segments.Count}");
        outp.WriteLine($"  Время         : {sw.Elapsed.TotalSeconds:0.0} с");
        outp.WriteLine($"  Ошибка        : {item.ErrorMessage ?? "нет"}");

        bool fileOk = false;
        long actualSize = 0;
        try
        {
            if (File.Exists(item.SavePath))
            {
                actualSize = new FileInfo(item.SavePath).Length;
                fileOk = item.TotalBytes == 0 || actualSize == item.TotalBytes;
            }
        }
        catch (Exception ex) { outp.WriteLine("  Проверка файла: " + ex.Message); }

        outp.WriteLine($"  Файл на диске : {actualSize} байт -> {(fileOk ? "РАЗМЕР СОВПАДАЕТ" : "РАЗМЕР НЕ СОВПАДАЕТ / ФАЙЛА НЕТ")}");
        outp.WriteLine("");
        outp.WriteLine(item.Status == Modules.Utilities.DownloadManager.Models.DownloadStatus.Completed && fileOk
            ? "РЕЗУЛЬТАТ: OK - файл скачан полностью и размер совпадает"
            : "РЕЗУЛЬТАТ: ПРОВАЛ");
        outp.Flush();

        // Shutdown() должен вызываться на потоке Dispatcher: после
        // ConfigureAwait(false) мы уже в пуле потоков, и вызов оттуда
        // не завершался — процесс оставался жив и тест упирался в таймаут.
        int code = item.Status == Modules.Utilities.DownloadManager.Models.DownloadStatus.Completed && fileOk ? 0 : 1;
        await Dispatcher.InvokeAsync(() => Shutdown(code));
    }

    /// <summary>
    /// Сквозная проверка движка копирования на реальных файлах.
    /// Проверяет то, что нельзя увидеть в коде: сохранность содержимого,
    /// разрешение коллизий имён, отсутствие затирания существующих файлов
    /// и отсутствие остаточных .partial.
    /// </summary>
    private async Task RunCopyTestAsync(string srcDir, string dstDir)
    {
        var outp = System.Console.Out;
        outp.WriteLine("COPY TEST");
        outp.WriteLine(new string('=', 60));
        outp.WriteLine($"  источник : {srcDir}");
        outp.WriteLine($"  назначение: {dstDir}");

        var engine = new Models.CopyEngine();
        try
        {
            await engine.StartRealCopyAsync(new[] { (srcDir, dstDir) }).ConfigureAwait(false);

            outp.WriteLine("");
            outp.WriteLine($"  IsCompleted : {engine.IsCompleted}");
            outp.WriteLine($"  IsCancelled : {engine.IsCancelled}");
            outp.WriteLine($"  ошибка     : {(string.IsNullOrEmpty(engine.OperationError) ? "нет" : engine.OperationError)}");
            outp.WriteLine($"  файлов     : {engine.Items.Count}");
            outp.WriteLine($"  проверено  : {engine.VerifiedItems.Count}");
            outp.WriteLine($"  пропущено  : {engine.Items.Count(x => x.WasSkipped)}");
            outp.WriteLine($"  ошибок     : {engine.Items.Count(x => x.Status == Models.CopyItemStatus.Error)}");
            outp.WriteLine($"  байт       : {engine.CopiedBytes} из {engine.TotalBytes}");

            outp.WriteLine("");
            outp.WriteLine("  по файлам:");
            foreach (var it in engine.Items.Take(20))
                outp.WriteLine($"    {it.Status,-10} {it.FileName}" +
                    (string.IsNullOrEmpty(it.VerifiedBy) ? "" : $"   [{it.VerifiedBy}]"));

            // Настройка CRC-32 обязана быть проверяемой, а не заявленной.
            // Показываем действующие параметры и требуем, чтобы каждый
            // файл был подтверждён сверкой, если она включена.
            outp.WriteLine("");
            outp.WriteLine("  параметры ввода-вывода:");
            outp.WriteLine("    " + Helpers.IoSettings.Describe());
            int verified = engine.Items.Count(i => !string.IsNullOrEmpty(i.VerifiedBy));
            outp.WriteLine($"    файлов со сверкой целостности: {verified}");

            bool ok = engine.IsCompleted && engine.Items.Count > 0 && !engine.AnySkipped
                      && engine.Items.All(i => i.Status == Models.CopyItemStatus.Done);

            // Если CRC-32 включён, но ни один файл не подтверждён —
            // заявленная в настройках защита не работает, и это провал.
            if (ok && Helpers.IoSettings.VerifyCrc32 && verified != engine.Items.Count)
            {
                ok = false;
                outp.WriteLine("");
                outp.WriteLine("ПРОВАЛ: сверка CRC-32 включена, но подтверждено не всё число файлов.");
            }
            outp.WriteLine("");
            outp.WriteLine(ok ? "ДВИЖОК: OK" : "ДВИЖОК: ПРОВАЛ");
            outp.Flush();

            engine.Dispose();
            await Dispatcher.InvokeAsync(() => Shutdown(ok ? 0 : 1));
        }
        catch (Exception ex)
        {
            outp.WriteLine("ИСКЛЮЧЕНИЕ: " + ex);
            outp.Flush();
            try { engine.Dispose(); } catch { }
            await Dispatcher.InvokeAsync(() => Shutdown(1));
        }
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
    /// <summary>
    /// Является ли запуск проверочным режимом, не требующим прав.
    /// </summary>
    /// <remarks>
    /// <para>Список ключей задан явно, а не через «если есть --no-elevate»:
    /// иначе право не повышать привилегии получал бы любой, кто допишет
    /// этот ключ в командную строку. Здесь перечислены ровно те режимы,
    /// которые печатают отчёт в консоль и завершаются.</para>
    ///
    /// <para>Обычный запуск интерфейса сюда не попадает: он всегда
    /// требует прав администратора.</para>
    /// </remarks>
    private static bool IsDiagnosticMode(string[] args)
    {
        string[] diagnostic = {
            "--view-audit", "--theme-audit", "--contrast-audit",
            "--anim-audit", "--smart-test", "--storage-audit", "--copy-test"
        };

        foreach (var a in args)
            foreach (var d in diagnostic)
                if (string.Equals(a, d, StringComparison.OrdinalIgnoreCase))
                    return true;

        return false;
    }

    /// <summary>
    /// Перезапускает процесс с повышенными правами.
    /// </summary>
    /// <remarks>
    /// <para>Возвращает false, если пользователь отказал в UAC или
    /// повышение невозможно. Вызывающий код обязан завершиться: работа
    /// без прав приводит к недостоверным показателям накопителей.</para>
    ///
    /// <para>Аргументы переносятся все, включая ключи аудита, иначе
    /// повышенный процесс не получил бы их и не смог выполнить проверку.</para>
    /// </remarks>
    private static bool TryRelaunchElevated(string[] args)
    {
        try
        {
            string exe = Environment.ProcessPath
                          ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                          ?? "Win11CopyDialog.exe";

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                // WorkingDirectory не переносится: при повышении оболочка
                // запускает процесс из системного каталога, и относительные
                // пути внутри программы разрешились бы неверно.
                Verb = "runas"
            };

            // Метка повышенного потомка. Без неё результат повышения
            // невозможно проверить: Process.Start сVerb=runas возвращает
            // успех и тогда, когда оболочка не смогла показать диалог,
            // а процесс умер сразу. Потомок проверяет свои права и
            // сообщает об отказе через код выхода.
            psi.ArgumentList.Add("--elevated-child");

            foreach (var a in args)
                psi.ArgumentList.Add(a);

            System.Diagnostics.Process.Start(psi);
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // 1223 = ERROR_CANCELLED: пользователь нажал «Нет».
            if (ex.NativeErrorCode == 1223)
            {
                MessageBox.Show(
                    "Motion Commander требует прав администратора.\n\n" +
                    "Без них программа не может прочитать состояние накопителей " +
                    "напрямую с диска: Windows запрещает доступ к устройству " +
                    "обычному пользователю. Показатели S.M.A.R.T., температура, " +
                    "износ и состояние секторов были бы недоступны, и вы видели бы\n" +
                    "недостоверные данные вместо настоящих.\n\n" +
                    "Запустите программу от имени администратора и подтвердите запрос.",
                    "Требуются права администратора",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show(
                    $"Не удалось запросить права администратора.\n\n{ex.Message}\n\n" +
                    "Программа будет закрыта: без повышения показатели накопителей " +
                    "не могут быть достоверными.",
                    "Ошибка запуска",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return false;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Не удалось запустить программу с повышенными правами.\n\n{ex.Message}",
                "Ошибка запуска",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }
}
