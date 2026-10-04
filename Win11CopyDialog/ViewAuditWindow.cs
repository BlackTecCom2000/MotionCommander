using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Win11CopyDialog;

/// <summary>
/// Создаёт и отрисовывает каждый экран приложения, перехватывая ошибки
/// привязок и разметки.
///
/// <para>Зачем это нужно. Сборка компилирует XAML в BAML, но привязки не
/// проверяет. Ошибка вида «TwoWayOneWayToSourceSource не может работать с
/// источником DownloadItem» или неэкранированные фигурные скобки в
/// StringFormat проявляются только при загрузке экрана и останавливают всё
/// приложение. Это окно превращает такое в отчёт вместо краша.</para>
/// </summary>
public sealed class ViewAuditWindow : Window
{
    private readonly List<string> _report = new();
    private int _passed;
    private int _failed;

    public ViewAuditWindow()
    {
        Title = "VIEW AUDIT";
        Width = 900;
        Height = 600;
        WindowStyle = WindowStyle.None;
        ShowInTaskbar = false;
        Opacity = 0.01;   // окно практически невидимо
        Content = new TextBlock { Text = "audit" };
    }

    public void BeginAudit() => Dispatcher.BeginInvoke(Run);

    /// <summary>
    /// Итог проверки экранов: сколько прошло, сколько нет и что именно.
    /// </summary>
    /// <remarks>
    /// Результат отделён от вывода в консоль, потому что вызывающий код
    /// может быть не тем, что печатает в консоль.
    /// </remarks>
    public sealed record ViewAuditResult(int Passed, int Failed, IReadOnlyList<string> Report)
    {
        /// <summary>Успешна ли проверка: не должно быть ни одного дефекта.</summary>
        public bool Ok => Failed == 0;
    }

    /// <summary>
    /// Выполняет проверку всех экранов и возвращает результат.
    /// </summary>
    /// <remarks>
    /// <para>Тот же самый прогон, что идёт по ключу <c>--view-audit</c>,
    /// одна и та же реализация. Дублировать перечень экранов во втором
    /// месте было бы прямой дорогой к расхождению: один список со временем
    /// перестал бы пополняться, и проверка тихо ослабла бы.</para>
    ///
    /// <para>Проверка не требует прав администратора. Экраны, читающие
    /// накопители или драйверы, без прав честно сообщают о недоступности
    /// и не подставляют значения — именно это и проверяется. Поэтому
    /// прогон доступен в сборочном конвейере, где диалог подтверждения
    /// не показывается.</para>
    /// </remarks>
    public static ViewAuditResult RunAll(TextWriter? output = null)
    {
        var holder = new ViewAuditWindow();

        TextWriter real = output ?? TextWriter.Null;
        var original = Console.Out;
        Console.SetOut(real);

        try
        {
            holder.Run();
            return new ViewAuditResult(holder._passed, holder._failed, holder._report.ToList());
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    private void Run()
    {
        var outp = Console.Out;
        outp.WriteLine("VIEW AUDIT - создание и отрисовка всех экранов");
        outp.WriteLine(new string('=', 70));

        var listener = BindingTraceListener.Instance;
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.All;
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        listener.Reset();

        try
        {
            // Окна
            Audit(outp, listener, "MainWindow", () => new MainWindow());
            Audit(outp, listener, "CopyDialogWindow", () => new CopyDialogWindow());
            Audit(outp, listener, "MotionCopyWindow", () => new MotionCopyWindow());
            Audit(outp, listener, "FileManagerWindow", () => new FileManagerWindow());
            Audit(outp, listener, "SettingsWindow", () => new Views.Dialogs.SettingsWindow());
            Audit(outp, listener, "VersionSelectDialog", () => new Views.Dialogs.VersionSelectDialog());
            Audit(outp, listener, "WizTreeAnalyzerWindow", () => new Views.Dialogs.WizTreeAnalyzerWindow());
            Audit(outp, listener, "DuplicateFinderWindow", () => new Views.Dialogs.DuplicateFinderWindow());
            Audit(outp, listener, "DriverInspectorWindow", () => new Views.Dialogs.DriverInspectorWindow());
            Audit(outp, listener, "CreateArchiveWindow", () => new Views.Dialogs.CreateArchiveWindow(new[] { @"C:\Windows\System32\notepad.exe" }));
            Audit(outp, listener, "ExtractArchiveWindow", () => new Views.Dialogs.ExtractArchiveWindow(@"C:\Windows\notepad.exe"));
            Audit(outp, listener, "AdvancedToolsWindow", () => new Views.Dialogs.AdvancedToolsWindow(@"C:\Windows\System32\notepad.exe"));
            Audit(outp, listener, "CyberFolderPickerDialog", () => new Views.Dialogs.CyberFolderPickerDialog(
                "test", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), 1000L));

            // Экраны-модули (UserControl)
            Audit(outp, listener, "DownloadManagerView", () => new Modules.Utilities.DownloadManager.Views.DownloadManagerView());
            Audit(outp, listener, "StorageControlCenterView", () => new Modules.StorageControlCenter.Views.StorageControlCenterView());
            Audit(outp, listener, "MigrationWizardView", () => new Modules.StorageControlCenter.Views.MigrationWizardView());
            Audit(outp, listener, "UninstallerView", () => new Modules.Utilities.Uninstaller.Views.UninstallerView());

            // Отдельно: загрузка данных в коллекции.
            //
            // Без наполненных списков шаблоны элементов НЕ применяются:
            // пустой ListView не создаёт контейнеры, привязки внутри
            // ItemTemplate не активируются, и ошибка в них остаётся
            // незамеченной. Именно так пропускалась ошибка
            // TwoWayOneWayToSourceSource в DownloadManagerView: список
            // загрузок был пуст, и привязка к Progress просто не
            // существовала в момент проверки.
            AuditWithItems(outp, listener, "DownloadManagerView (с данными)",
                () => new Modules.Utilities.DownloadManager.Views.DownloadManagerView());
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
        }

        outp.WriteLine(new string('=', 70));
        outp.WriteLine($"Экранов проверено: {_passed + _failed}   успешно: {_passed}   с ошибками: {_failed}");

        if (_report.Count > 0)
        {
            outp.WriteLine("");
            outp.WriteLine("НАЙДЕННЫЕ ПРОБЛЕМЫ:");
            foreach (var r in _report.Take(50)) outp.WriteLine("  " + r);
        }

        outp.WriteLine(_failed == 0 ? "РЕЗУЛЬТАТ: OK" : "РЕЗУЛЬТАТ: ЕСТЬ ОШИБКИ");
        outp.Flush();

        int code = _failed == 0 ? 0 : 1;
        Close();
        Application.Current.Shutdown(code);
    }

    /// <summary>
    /// Описывает исключение вместе со всей цепочкой вложенных.
    /// </summary>
    /// <remarks>
    /// <para>Сообщение вида «предоставление значения для
    /// StaticResourceExtension вызвало исключение» бесполезно: оно не
    /// называет ни ключ, ни словарь. Настоящая причина лежит во
    /// вложенном исключении — обычно это «Не удалось найти ресурс с
    /// именем <c>Имя</c>».</para>
    ///
    /// <para>Без цепочки поиск ключа в словарях означал бы
    /// перебор: два экрана из восемнадцати падали с одинаковым
    /// сообщением, и найти виновный ключ можно было только
    /// перекладыванием.</para>
    /// </remarks>
    private static string Describe(Exception ex)
    {
        var parts = new List<string>();
        var seen = new HashSet<Exception>();

        for (Exception? e = ex; e != null && seen.Add(e); e = e.InnerException)
        {
            string text = e.Message?.Split('\n')[0].Trim() ?? "";
            parts.Add(e.GetType().Name + ": " + text);
        }

        return string.Join(" -> ", parts);
    }

    private void Audit(TextWriter outp, BindingTraceListener listener, string name, Func<FrameworkElement> factory)
    {
        listener.Reset();
        string error = "";
        FrameworkElement? element = null;

        try
        {
            element = factory();
            ForceLayout(element);
        }
        catch (Exception ex)
        {
            error = Describe(ex);
        }
        finally
        {
            try { if (element is Window win) win.Close(); } catch { }
        }

        var bindingErrors = listener.Snapshot();
        bool hasBindingErrors = bindingErrors.Length > 0;

        if (error.Length == 0 && !hasBindingErrors)
        {
            _passed++;
            outp.WriteLine($"  OK      {name}");
        }
        else
        {
            _failed++;
            outp.WriteLine($"  FAIL    {name}" +
                (error.Length > 0 ? "  -> " + error : $"  -> {bindingErrors.Length} ошибок привязки"));
            _report.Add($"{name}: " + (error.Length > 0 ? error : "ошибки привязки"));
            foreach (var b in bindingErrors.Take(3))
                _report.Add($"    {b}");
        }
    }

    /// <summary>
    /// Проверка экрана, у которого нужно наполнить коллекцию данных.
    /// Без этого шаблоны элементов не применяются и ошибки привязок
    /// внутри них невозможно обнаружить.
    /// </summary>
    private void AuditWithItems(TextWriter outp, BindingTraceListener listener, string name, Func<FrameworkElement> factory)
    {
        listener.Reset();
        string error = "";
        FrameworkElement? element = null;

        try
        {
            element = factory();

            // Наполняем список загрузок настоящими объектами, включая
            // сегменты: именно они создают контейнеры и применяют ItemTemplate.
            if (element.DataContext is Modules.Utilities.DownloadManager.ViewModels.DownloadManagerViewModel vm)
            {
                var item = new Modules.Utilities.DownloadManager.Models.DownloadItem
                {
                    Url = "https://example.com/file.bin",
                    FileName = "file.bin",
                    SavePath = "C:\\Temp\\file.bin",
                    TotalBytes = 1024 * 1024,
                    Status = Modules.Utilities.DownloadManager.Models.DownloadStatus.Downloading,
                };
                item.BytesDownloaded = 512 * 1024;

                for (int i = 0; i < 4; i++)
                {
                    item.Segments.Add(new Modules.Utilities.DownloadManager.Models.DownloadSegment
                    {
                        DownloadItemId = item.Id,
                        OwnerDownloadItem = item,
                        Index = i,
                        StartPosition = i * 262144,
                        EndPosition = (i + 1) * 262144 - 1,
                        BytesDownloaded = 131072,
                        Status = Modules.Utilities.DownloadManager.Models.SegmentStatus.Downloading,
                    });
                }

                vm.Downloads.Add(item);
                vm.SelectedDownload = item;
            }

            ForceLayout(element);
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            try { if (element is Window win) win.Close(); } catch { }
        }

        var bindingErrors = listener.Snapshot();
        if (error.Length == 0 && bindingErrors.Length == 0)
        {
            _passed++;
            outp.WriteLine($"  OK      {name}");
        }
        else
        {
            _failed++;
            outp.WriteLine($"  FAIL    {name}" +
                (error.Length > 0 ? "  -> " + error : $"  -> {bindingErrors.Length} ошибок привязки"));
            _report.Add($"{name}: " + (error.Length > 0 ? error : "ошибки привязки"));
            foreach (var b in bindingErrors.Take(4))
                _report.Add($"    {b}");
        }
    }

    /// <summary>
    /// Принудительно прогоняет измерение и компоновку.
    /// Без этого визуальное дерево может не построиться, и ошибки привязки
    /// не проявятся до первого реального показа окна.
    /// </summary>
    private static void ForceLayout(FrameworkElement element)
    {
        element.Measure(new Size(1400, 900));
        element.Arrange(new Rect(0, 0, 1400, 900));
        element.UpdateLayout();

        // Второй проход: первый строит шаблоны, второй применяет привязки
        // к уже созданным элементам списков.
        element.Measure(new Size(1400, 900));
        element.Arrange(new Rect(0, 0, 1400, 900));
        element.UpdateLayout();

        PumpDispatcher();
        UpdateAll(element);
    }

    private static void UpdateAll(DependencyObject root)
    {
        int n;
        try { n = VisualTreeHelper.GetChildrenCount(root); }
        catch { return; }

        for (int i = 0; i < n; i++)
        {
            DependencyObject? child;
            try { child = VisualTreeHelper.GetChild(root, i); } catch { continue; }
            if (child == null) continue;

            if (child is FrameworkElement fe)
            {
                try { fe.UpdateLayout(); } catch { }
            }
            UpdateAll(child);
        }
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}

/// <summary>
/// Считает ошибки привязок, которые WPF пишет в Trace.
/// Привязка, не нашедшая свойство, приводит к остановке всего приложения,
/// поэтому такие ошибки нужно находить автоматически.
/// </summary>
internal sealed class BindingTraceListener : TraceListener
{
    public static readonly BindingTraceListener Instance = new();

    private readonly List<string> _errors = new();
    private readonly object _lock = new();

    public override void Write(string? message) { }
    public override void WriteLine(string? message) { }

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message)
    {
        if (string.IsNullOrEmpty(message)) return;
        if (!message.Contains("Binding", StringComparison.OrdinalIgnoreCase)) return;

        lock (_lock)
        {
            _errors.Add(message.Length > 500 ? message[..500] : message);
            if (_errors.Count > 200) _errors.RemoveAt(0);
        }
    }

    public string[] Snapshot() { lock (_lock) return _errors.ToArray(); }
    public void Reset() { lock (_lock) _errors.Clear(); }
}
