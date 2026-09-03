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
        string crashLog = @"C:\Users\djabo\.gemini\antigravity-ide\brain\ab0105fa-d21c-4725-bc46-bf6fbdc2e347\scratch\crash.txt";
        AppDomain.CurrentDomain.UnhandledException += (s, ev) => {
            try { System.IO.File.WriteAllText(crashLog, ev.ExceptionObject?.ToString() ?? "null"); } catch {}
        };
        DispatcherUnhandledException += (s, ev) => {
            try { System.IO.File.WriteAllText(crashLog, ev.Exception?.ToString() ?? "null"); } catch {}
        };

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
            string targetDir = @"F:\ANTIGRAVITY\WIN11 COPY\bench_temp";
            int idx = Array.IndexOf(e.Args, "--bench-cli");
            if (idx >= 0 && idx + 1 < e.Args.Length && !e.Args[idx + 1].StartsWith("--"))
            {
                targetDir = e.Args[idx + 1];
            }

            try
            {
                var report = Task.Run(async () => await Modules.PerformanceEngine.BenchmarkEngine.RunFullBenchmarkAsync(targetDir)).GetAwaiter().GetResult();
                
                string outJson = System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                string outPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "benchmark_last_run.json");
                System.IO.File.WriteAllText(outPath, outJson);
            }
            catch (Exception ex)
            {
                string errPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "benchmark_error.txt");
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
        else if (e.Args.Contains("--advanced-tools-demo"))
        {
            new Views.Dialogs.AdvancedToolsWindow(@"C:\Windows\System32\notepad.exe").Show();
        }
        else if (e.Args.Contains("--tab-transfer"))
        {
            var main = new MainWindow();
            main.SelectTab(1);
            main.Show();
        }
        else if (e.Args.Contains("--tab-storage"))
        {
            var main = new MainWindow();
            main.SelectTab(2);
            main.Show();
        }
        else if (e.Args.Contains("--tab-diagnostics"))
        {
            var main = new MainWindow();
            main.SelectTab(3);
            main.Show();
        }
        else if (e.Args.Contains("--tab-tools"))
        {
            var main = new MainWindow();
            main.SelectTab(4);
            main.Show();
        }
        else if (e.Args.Contains("--tab-settings-bottom"))
        {
            var main = new MainWindow();
            main.SelectTab(5);
            main.Loaded += (_, _) => main.ScrollSettingsToBottom();
            main.Show();
        }
        else if (e.Args.Contains("--tab-settings"))
        {
            var main = new MainWindow();
            main.SelectTab(5);
            main.Show();
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
}

