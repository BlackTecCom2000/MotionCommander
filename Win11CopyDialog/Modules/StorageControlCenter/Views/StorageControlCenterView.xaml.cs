using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Ellipse = System.Windows.Shapes.Ellipse;
using Win11CopyDialog.Helpers;
using Win11CopyDialog.Controls;
using Win11CopyDialog.Modules.StorageControlCenter.Models;
using Win11CopyDialog.Modules.StorageControlCenter.Services;

namespace Win11CopyDialog.Modules.StorageControlCenter.Views;

public partial class StorageControlCenterView : UserControl
{
    private List<StorageDisk> _disks = new();
    private StorageDisk? _selectedDisk;
    private StoragePartition? _selectedPartition;
    private readonly DispatcherTimer _telemetryTimer;

    /// <summary>
    /// «Живые» иконки карточек накопителей, ключ — номер диска.
    /// Заполняются реальными значениями телеметрии при каждом опросе.
    /// </summary>
    private readonly Dictionary<int, LiveDiskIcon> _liveIcons = new();
    private CancellationTokenSource? _benchCts;
    private CancellationTokenSource? _wipeCts;

    // Поля интерактивной формы Partition Manager
    private string _currentAction = "";
    private TextBox? _inputSizeMb;
    private TextBox? _inputLabel;
    private ComboBox? _comboFs;
    private ComboBox? _comboLetter;
    private ComboBox? _comboCluster;
    private CheckBox? _chkQuick;

    public StorageControlCenterView()
    {
        InitializeComponent();

        _telemetryTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2.0)
        };
        _telemetryTimer.Tick += TelemetryTimer_Tick;

        Loaded += StorageControlCenterView_Loaded;
        Unloaded += StorageControlCenterView_Unloaded;
    }

    public void SelectSubTab(int index)
    {
        RadioButton target = index switch
        {
            1 => SubTabPartitionsRadio,
            2 => SubTabBenchmarkRadio,
            3 => SubTabOptimizerRadio,
            4 => SubTabCleanupRadio,
            5 => SubTabSafetyRadio,
            6 => SubTabMigrationRadio,
            _ => SubTabHealthRadio
        };
        target.IsChecked = true;
        SubTab_Checked(target, new RoutedEventArgs());
    }

    private async void StorageControlCenterView_Loaded(object sender, RoutedEventArgs e)
    {
        // Раньше здесь не было try/catch: любое исключение из RefreshDisksAsync
        // (WMI/SMART отдают их регулярно) всплывало в глобальный обработчик —
        // пользователь получал модальное окно ошибки просто при открытии вкладки.
        try
        {
            await RefreshDisksAsync();

            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--disk" && i + 1 < args.Length && int.TryParse(args[i + 1], out int dIdx))
                {
                    var target = _disks.FirstOrDefault(d => d.DiskNumber == dIdx);
                    if (target != null) SelectDisk(target);
                    else if (dIdx >= 0 && dIdx < _disks.Count) SelectDisk(_disks[dIdx]);
                }
            }

            if (args.Contains("--subtab-partitions") || args.Contains("--partitions"))
            {
                SelectSubTab(1);
            }
            else if (args.Contains("--subtab-benchmark"))
            {
                SelectSubTab(2);
            }
            else if (args.Contains("--subtab-optimizer"))
            {
                SelectSubTab(3);
            }
            else if (args.Contains("--subtab-cleanup"))
            {
                SelectSubTab(4);
            }
            else if (args.Contains("--subtab-safety"))
            {
                SelectSubTab(5);
            }
        }
        catch (Exception)
        {
            // Вкладка остаётся рабочей: карточки пустые, но ничего не сломано.
            // Подробности пользователь увидит при следующем нажатии «Обновить».
        }
        finally
        {
            // Таймер телеметрии запускается всегда — TelemetryTimer_Tick сам
            // выходит при пустом _disks, зависшего UI на ошибке опроса нет.
            if (_telemetryTimer.IsEnabled == false) _telemetryTimer.Start();
        }
    }

    private void StorageControlCenterView_Unloaded(object sender, RoutedEventArgs e)
    {
        _telemetryTimer.Stop();

        // Гонка: CTS принадлежит запущенной операции, а не этому обработчику.
        // Здесь можно только ПРОСИТЬ об отмене. Освобождать источник нельзя:
        // фоновая задача всё ещё держит его токен и получит
        // ObjectDisposedException на ct.ThrowIfCancellationRequested().
        // Поле и Dispose делает владелец в своём finally.
        SafeCancel(Volatile.Read(ref _benchCts));
        SafeCancel(Volatile.Read(ref _wipeCts));

        // Раньше CTS отменялись, но НЕ обнулялись. Обработчики кнопок
        // используют «_benchCts != null» как признак «тест идёт», поэтому после
        // переключения вкладки кнопка оставалась с надписью «ОСТАНОВИТЬ ТЕСТ».
        // Теперь поле чистит finally операции, поэтому метку кнопки можно
        // вернуть сразу — новый запуск всё равно заблокирован до конца
        // отменяемой операции.
        ResetRunningOperationsUi();
    }

    /// <summary>
    /// Отмена источника токена, который уже мог быть освобождён владельцем.
    /// Без этой обёртки Cancel() на освобождённом CTS ронял обработчик
    /// кнопки/окна с ObjectDisposedException.
    /// </summary>
    private static void SafeCancel(CancellationTokenSource? cts)
    {
        if (cts == null) return;
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Владелец уже освободил источник в своём finally — это нормально.
        }
        catch (AggregateException)
        {
            // Исключение из callback'а отмены не должно ронять UI.
        }
    }

    /// <summary>
    /// Возвращает UI операций (бенчмарк/очистка) в исходное состояние.
    /// Поля _benchCts/_wipeCts здесь НЕ трогаются: ими владеют операции,
    /// обнуляющие поле через Interlocked.CompareExchange в своём finally.
    /// </summary>
    private void ResetRunningOperationsUi()
    {
        if (StartBenchBtn != null)
        {
            StartBenchBtn.Content = "🚀 Запустить бенчмарк";
            StartBenchBtn.IsEnabled = true;
        }

        if (StartWipeBtn != null)
        {
            StartWipeBtn.Content = "🧹 Запустить безопасную очистку";
            StartWipeBtn.IsEnabled = true;
        }

        if (BenchProgressCard != null) BenchProgressCard.Visibility = Visibility.Collapsed;
    }

    public async Task RefreshDisksAsync()
    {
        DrivesStripPanel.Children.Clear();
        var loadingText = new TextBlock
        {
            Text = "Опрос физических накопителей и S.M.A.R.T. контроллеров...",
            FontSize = 11,
            Foreground = (Brush)FindResource("AccentBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 8, 10, 8)
        };
        DrivesStripPanel.Children.Add(loadingText);

        _disks = await Task.Run(() => StorageDiscoveryService.GetAllDisks(forceRefresh: true));

        DrivesStripPanel.Children.Clear();

        foreach (var disk in _disks)
        {
            var card = CreateDiskCard(disk);
            DrivesStripPanel.Children.Add(card);
        }

        if (_disks.Count > 0)
        {
            SelectDisk(_selectedDisk != null ? _disks.FirstOrDefault(d => d.DiskNumber == _selectedDisk.DiskNumber) ?? _disks[0] : _disks[0]);
        }

        // Загрузка категорий очистки
        LoadCleanupCategories();
        MigrationWizardComponent.SetDisks(_disks);

        // Фрагментация измеряется ОТДЕЛЬНО и в фоне.
        // Раньше `defrag /A` запускался синхронно внутри GetAllDisks и
        // подвешивал вкладку: процесс зависает при запуске из GUI без
        // консоли, и отмена не помогает. Теперь это фоновая задача с
        // результатом «Нет данных», если дефрагментатор не ответил.
        _ = MeasureFragmentationInBackgroundAsync();
    }

    private CancellationTokenSource? _fragmentationCts;

    /// <summary>
    /// Фоновое измерение фрагментации. Диски обновляются на месте,
    /// поэтому перерисовываем панель только по завершении.
    /// </summary>
    private async Task MeasureFragmentationInBackgroundAsync()
    {
        var disks = _disks;
        if (disks == null || disks.Count == 0) return;

        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _fragmentationCts, cts);

        // Гонка (была): previous?.Dispose() здесь освобождал источник, который
        // ПРЕДЫДУЩИЙ замер всё ещё использовал внутри вызова процесса defrag —
        // тот получал ObjectDisposedException на своём токене. Освобождать
        // источник имеет право только тот поток, который его создал,
        // поэтому ниже previous лишь отменяется, а Dispose делает его владелец
        // в собственном finally.
        SafeCancel(previous);

        var token = cts.Token;
        bool wasCanceled;

        try
        {
            await StorageDiscoveryService.AnalyzeFragmentationAsync(disks, progress: null, token);
            wasCanceled = false;
        }
        catch (OperationCanceledException)
        {
            wasCanceled = true;
            return;
        }
        catch
        {
            // Фрагментация — справочные данные; её отсутствие не должно
            // ломать интерфейс. HasFragmentation остаётся снятым.
            wasCanceled = true;
            return;
        }
        finally
        {
            // Поле обнуляется только если в нём всё ещё наш источник:
            // иначе можно было бы стереть токен уже НОВОГО замера.
            SafeCancel(cts);
            Interlocked.CompareExchange(ref _fragmentationCts, null, cts);
            cts.Dispose();
        }

        if (wasCanceled) return;
        if (!IsLoaded) return;

        // Результат применяем на UI-потоке.
        await Dispatcher.InvokeAsync(() =>
        {
            if (_disks == null) return;
            foreach (var d in _disks)
            {
                // Оценка пересчитывается, потому что фрагментация влияет
                // на рекомендации, даже если не влияет на сам балл.
                StorageAdvisorService.EvaluateScore(d);
            }

            // Словарь иконок очищается вместе с карточками: иначе он
            // удерживал бы удалённые элементы и накапливал мусор при
            // каждом обновлении списка накопителей.
            _liveIcons.Clear();
            DrivesStripPanel.Children.Clear();
            foreach (var d in _disks)
                DrivesStripPanel.Children.Add(CreateDiskCard(d));

            // Каскад появления карточек. Без него все накопители
            // возникают одновременно, и это читается как мигание.
            // Проверка на видимость обязательна: иначе анимация
            // запускается на скрытой панели и зря будит систему рендеринга.
            if (IsVisible)
                Stagger.AnimateEntrance(DrivesStripPanel, stepMs: 30, durationMs: 240);

            if (_selectedDisk != null)
            {
                var again = _disks.FirstOrDefault(d => d.DiskNumber == _selectedDisk.DiskNumber);
                if (again != null) SelectDisk(again);
            }
        });
    }

    private UIElement CreateDiskCard(StorageDisk disk)
    {
        var border = new Border
        {
            Width = 225,
            Height = 96,
            CornerRadius = new CornerRadius(9),
            Background = (Brush)FindResource("CardBackgroundBrush"),
            BorderBrush = (Brush)FindResource("CardBorderBrush"),
            BorderThickness = new Thickness(1.5),
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = Cursors.Hand,
            Tag = disk,
            SnapsToDevicePixels = true
        };

        var mainGrid = new Grid();
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // --- ROW 0: Аппаратный бейдж шины + Здоровье S.M.A.R.T. + Температура ---
        var row0 = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };

        // «Живая» иконка накопителя. Кольцо занятости, скорость вращения
        // и стрелки чтения/записи питаются РЕАЛЬНЫМИ значениями из
        // Win32_PerfFormattedData_PerfDisk_PhysicalDisk, а не декоративной
        // анимацией: у простаивающего диска иконка неподвижна.
        var liveIcon = new LiveDiskIcon
        {
            IconSize = 26,
            ShowFlow = true,
            Width = 26,
            Height = 26,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        _liveIcons[disk.DiskNumber] = liveIcon;
        DockPanel.SetDock(liveIcon, Dock.Left);
        row0.Children.Add(liveIcon);

        (string typeLabel, Brush typeBrush, Brush typeBg) = disk.MediaType switch
        {
            StoragePhysicalMedia.NVMeSSD => ($"#{disk.DiskNumber} NVMe PCIe", (Brush)FindResource("MediaNVMeBrush"), (Brush)FindResource("MediaNVMeBackground")),
            StoragePhysicalMedia.SataSSD => ($"#{disk.DiskNumber} SATA SSD", (Brush)FindResource("MediaSsdBrush"), (Brush)FindResource("MediaSsdBackground")),
            StoragePhysicalMedia.HDD => ($"#{disk.DiskNumber} HDD", (Brush)FindResource("MediaHddBrush"), (Brush)FindResource("MediaHddBackground")),
            StoragePhysicalMedia.USBFlash => ($"#{disk.DiskNumber} USB 3.0", (Brush)FindResource("MediaUsbBrush"), (Brush)FindResource("MediaUsbBackground")),
            _ => ($"#{disk.DiskNumber} DISK", (Brush)FindResource("AccentBrush"), (Brush)FindResource("ChipBackgroundBrush"))
        };

        var typeBadge = new Border
        {
            Background = typeBg,
            BorderBrush = typeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 1, 5, 1),
            VerticalAlignment = VerticalAlignment.Center
        };
        typeBadge.Child = new TextBlock
        {
            Text = typeLabel,
            FontSize = 8.5,
            FontWeight = FontWeights.Bold,
            Foreground = typeBrush
        };
        DockPanel.SetDock(typeBadge, Dock.Left);
        row0.Children.Add(typeBadge);

        // Правый блок статусов
        var statusStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(statusStack, Dock.Right);

        // Индикатор здоровья.
        // Раньше здесь безусловно показывались буква оценки и её цвет, даже когда
        // оценка не рассчитана (нет измеренных данных), а также «0°C» для
        // непомеренной температуры. Пользователь видел «A+ 0°C» на диске,
        // о котором приложение ничего не знает. Теперь при отсутствии данных
        // показывается «н/д» нейтральным цветом, а не выдуманная оценка.
        var healthPill = new Border
        {
            Background = (Brush)FindResource("ChipBackgroundBrush"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4, 1, 4, 1),
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var healthStack = new StackPanel { Orientation = Orientation.Horizontal };

        bool scoreKnown = disk.Score.IsCalculated;
        // Цвет точки: только если оценка реально посчитана, иначе нейтральный.
        var dotColor = scoreKnown
            ? (disk.Score.TotalScore >= 80 ? Color.FromRgb(16, 185, 129)
             : disk.Score.TotalScore >= 60 ? Color.FromRgb(245, 158, 11)
             : Color.FromRgb(239, 68, 68))
            : Color.FromRgb(120, 130, 150);

        healthStack.Children.Add(new Ellipse
        {
            Width = 5,
            Height = 5,
            Fill = new SolidColorBrush(dotColor),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 3, 0)
        });
        healthStack.Children.Add(new TextBlock
        {
            Text = scoreKnown ? disk.Score.Grade : "н/д",
            FontSize = 8.5,
            FontWeight = FontWeights.Bold,
            Foreground = scoreKnown
                ? (Brush)FindResource("PrimaryTextBrush")
                : (Brush)FindResource("SecondaryTextBrush"),
            ToolTip = scoreKnown
                ? $"Оценка {disk.Score.TotalScore:F0}/100 по измеренным параметрам"
                : "Оценка не рассчитана: контроллер не отдаёт измеримые параметры"
        });
        healthPill.Child = healthStack;
        statusStack.Children.Add(healthPill);

        // Температура: только если реально измерена.
        if (disk.HasTemperature)
        {
            var tempBrush = new BrushConverter().ConvertFromString(disk.TemperatureColor) as Brush ?? (Brush)FindResource("PrimaryTextBrush");
            var tempPill = new Border
            {
                Background = (Brush)FindResource("ChipBackgroundBrush"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4, 1, 4, 1),
                VerticalAlignment = VerticalAlignment.Center
            };
            tempPill.Child = new TextBlock
            {
                Text = disk.TemperatureDisplay,
                FontSize = 8.5,
                FontWeight = FontWeights.Bold,
                Foreground = tempBrush,
                ToolTip = disk.TemperatureSourceDescription
            };
            statusStack.Children.Add(tempPill);
        }
        else
        {
            var tempPill = new Border
            {
                Background = (Brush)FindResource("ChipBackgroundBrush"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4, 1, 4, 1),
                VerticalAlignment = VerticalAlignment.Center
            };
            tempPill.Child = new TextBlock
            {
                Text = "°C: н/д",
                FontSize = 8.5,
                Foreground = (Brush)FindResource("SecondaryTextBrush"),
                ToolTip = "Температура недоступна: контроллер диска не публикует датчик"
            };
            statusStack.Children.Add(tempPill);
        }

        row0.Children.Add(statusStack);
        Grid.SetRow(row0, 0);
        mainGrid.Children.Add(row0);

        // --- ROW 1: Название модели диска + Буквы томов + Уникальный идентификатор ---
        var row1 = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        var modelPanel = new DockPanel();
        var driveBadge = new Border
        {
            Background = (Brush)FindResource("ChipBackgroundBrush"),
            BorderBrush = (Brush)FindResource("AccentBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 0, 4, 0),
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        driveBadge.Child = new TextBlock
        {
            Text = disk.DriveLettersFormatted,
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("AccentBrush")
        };
        DockPanel.SetDock(driveBadge, Dock.Left);
        modelPanel.Children.Add(driveBadge);

        var modelText = new TextBlock
        {
            Text = disk.Model,
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)FindResource("PrimaryTextBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        modelPanel.Children.Add(modelText);
        row1.Children.Add(modelPanel);

        // Уникальный серийный номер и шина для гарантированного отличия идентичных SSD
        var idText = new TextBlock
        {
            Text = disk.HardwareIdentity + (disk.Partitions.Count > 0 ? $" • {disk.Partitions.Count} разд." : ""),
            FontSize = 8.5,
            Style = (Style)FindResource("MutedText"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 1, 0, 0)
        };
        row1.Children.Add(idText);

        Grid.SetRow(row1, 1);
        mainGrid.Children.Add(row1);

        // --- ROW 2: Полоса заполнения и емкость ---
        var row2 = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
        var pBar = new ProgressBar
        {
            Height = 4,
            Minimum = 0,
            Maximum = 100,
            Value = disk.UsedSpacePercent,
            Foreground = disk.UsedSpacePercent > 90 ? new SolidColorBrush(Color.FromRgb(239, 68, 68)) : (disk.UsedSpacePercent > 80 ? new SolidColorBrush(Color.FromRgb(245, 158, 11)) : typeBrush),
            Background = (Brush)FindResource("WindowBackgroundBrush"),
            Margin = new Thickness(0, 0, 0, 2)
        };
        row2.Children.Add(pBar);

        var capDock = new DockPanel();
        capDock.Children.Add(new TextBlock
        {
            Text = $"{disk.FreeSpaceFormatted} своб.",
            FontSize = 8.5,
            Style = (Style)FindResource("MutedText")
        });
        var totalText = new TextBlock
        {
            Text = $"{disk.TotalSizeFormatted} ({disk.UsedSpacePercent:F0}%)",
            FontSize = 8.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("PrimaryTextBrush"),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        DockPanel.SetDock(totalText, Dock.Right);
        capDock.Children.Add(totalText);
        row2.Children.Add(capDock);

        Grid.SetRow(row2, 2);
        mainGrid.Children.Add(row2);

        border.Child = mainGrid;

        border.ToolTip = new ToolTip
        {
            Content = $"Накопитель: {disk.Model}\nТома: {disk.DriveLettersFormatted}\nДиск: #{disk.DiskNumber} • Серийный номер: {disk.SerialNumber}\nИнтерфейс: {disk.BusTypeString} • Разметка: {disk.PartitionsSummary}\nЕмкость: {disk.TotalSizeFormatted} (Свободно: {disk.FreeSpaceFormatted})\nЗдоровье: {disk.Score.TotalScore:F0}/100 ({disk.Score.Grade})\nТемпература: {disk.TemperatureFormatted}"
        };

        border.MouseEnter += (s, e) =>
        {
            if (_selectedDisk != disk) border.BorderBrush = (Brush)FindResource("GlassBorderBrush");
        };
        border.MouseLeave += (s, e) =>
        {
            if (_selectedDisk != disk) border.BorderBrush = (Brush)FindResource("CardBorderBrush");
        };
        border.PreviewMouseLeftButtonDown += (s, e) =>
        {
            SelectDisk(disk);
            e.Handled = true;
        };

        return border;
    }

    private void SelectDisk(StorageDisk disk)
    {
        _selectedDisk = disk;

        // Обновление подсветки карточек (Zero-Conflict)
        foreach (var child in DrivesStripPanel.Children)
        {
            if (child is Border b && b.Tag is StorageDisk d)
            {
                bool isSel = d.DiskNumber == disk.DiskNumber;
                b.BorderBrush = isSel ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("CardBorderBrush");
                b.Background = isSel ? (Brush)FindResource("ChipBackgroundBrush") : (Brush)FindResource("CardBackgroundBrush");
                b.Effect = isSel ? new DropShadowEffect
                {
                    BlurRadius = 12,
                    ShadowDepth = 0,
                    Color = Color.FromRgb(0, 240, 255),
                    Opacity = 0.55
                } : null;
                if (isSel) b.BringIntoView();
            }
        }

        // ── Оценка состояния ────────────────────────────────────────────────
        // Раньше здесь безусловно выводилось «A+» и «98 / 100», потому что
        // в модели стояли значения по умолчанию. Теперь при отсутствии
        // измерений показывается честная «н/д».
        HealthScoreGradeText.Text = disk.Score.Grade;
        HealthScoreValueText.Text = disk.Score.IsCalculated
            ? $"{disk.Score.TotalScore:F0} / 100"
            : "н/д";
        HealthScoreStatusText.Text = disk.Score.StatusText;
        HealthScoreStatusText.Foreground = new BrushConverter().ConvertFromString(disk.Score.StatusColor) as Brush;

        // На чём именно основан вердикт. Без этой строки «A+» выглядит как
        // факт, хотя зачастую измерен был один параметр из четырёх.
        if (HealthScoreBasisText != null)
        {
            HealthScoreBasisText.Text = disk.Score.IsCalculated
                ? $"{disk.Score.BasisDescription} · уверенность {disk.Score.MeasuredComponentCount}/4"
                : "Ни один показатель состояния не измерен — оценка не выставляется";
        }

        // ── Температура: только реальное измерение ──────────────────────────
        DiskTempValueText.Text = disk.TemperatureFormatted;
        TempStatusBadgeText.Text = !disk.HasTemperature ? "Нет данных"
            : disk.TemperatureC < 50 ? "Норма"
            : disk.TemperatureC < 65 ? "Внимание"
            : "Троттлинг";
        TempBadgeBorder.Background = new BrushConverter().ConvertFromString(disk.TemperatureColor) as Brush;
        TempDescText.Text = disk.TemperatureStatus;

        // ── Износ и ресурс ─────────────────────────────────────────────────
        DiskWearValueText.Text = disk.WearFormatted;
        DiskLifeDescText.Text = disk.TotalWrittenFormatted;

        // ── Наработка и циклы ──────────────────────────────────────────────
        DiskPowerHoursText.Text = disk.PowerOnHoursFormatted;
        DiskPowerCyclesText.Text = disk.PowerCyclesFormatted;

        // ── Ёмкость: реальные данные разделов ─────────────────────────────
        DiskCapacitySummaryText.Text = $"Емкость: {Formatters.Bytes(disk.TotalSizeBytes - (long)disk.TotalFreeBytes)} занято из {disk.TotalSizeFormatted} ({disk.FreeSpacePercent:F1}% свободно)";
        DiskFreeSpaceBadgeText.Text = $"Свободно: {disk.FreeSpaceFormatted}";
        DiskSpaceProgressBar.SetSafe(disk.UsedSpacePercent);

        // ── SMART: показываем только реально прочитанные атрибуты ─────────
        if (disk.HasSmartAttributes && disk.SmartAttributes.Count > 0)
        {
            SmartAttributesList.ItemsSource = disk.SmartAttributes;
            if (SmartEmptyText != null) SmartEmptyText.Visibility = Visibility.Collapsed;

            // Итог самотеста и состояние секторов — это выводы из тех же
            // прочитанных атрибутов, а не отдельный запрос.
            RenderSmartVerdict(disk);
        }
        else
        {
            SmartAttributesList.ItemsSource = null;

            if (SmartEmptyText != null)
            {
                SmartEmptyText.Visibility = Visibility.Visible;

                // Текст различается по ПРИЧИНЕ отсутствия данных. Раньше
                // во всех случаях писалось одно и то же, поэтому
                // невозможность чтения выглядела как отсутствие данных
                // на диске.
                if (disk.SmartNeedsAdministrator)
                {
                    SmartEmptyText.Text =
                        "Не удалось прочитать S.M.A.R.T.: нужны права администратора.\n\n" +
                        "S.M.A.R.T. читается напрямую с накопителя командой ATA, а Windows " +
                        "запрещает открывать накопитель обычному пользователю.\n\n" +
                        "Запустите Motion Commander от имени администратора — данные появятся.";
                }
                else if (disk.TelemetryNote.Length > 0)
                {
                    SmartEmptyText.Text = "S.M.A.R.T. недоступен.\n" + disk.TelemetryNote;
                }
                else
                {
                    SmartEmptyText.Text =
                        "S.M.A.R.T. недоступен: контроллер не публикует предиктивные данные.\n" +
                        "Остальные показатели накопителя измерены настоящим образом.";
                }
            }

            if (SmartVerdictBorder != null) SmartVerdictBorder.Visibility = Visibility.Collapsed;
        }

        // Рекомендации Storage AI Advisor
        PopulateAdvisorRecommendations();

        // Карта разделов
        RenderPartitionMap(disk);

        // Бенчмарк: заполняем список томов для теста
        BenchTargetDriveCombo.Items.Clear();
        foreach (var p in disk.Partitions.Where(p => !string.IsNullOrEmpty(p.DriveLetter)))
        {
            BenchTargetDriveCombo.Items.Add($"{p.DriveLetter}:\\");
        }
        if (BenchTargetDriveCombo.Items.Count > 0) BenchTargetDriveCombo.SelectedIndex = 0;

        // Оптимизатор
        OptimizerDriveTypeText.Text = $"Обнаружен накопитель: {disk.Model} [{disk.MediaTypeString} • {disk.BusTypeString}]";
    }

    /// <summary>
    /// Показывает вывод о состоянии накопителя, собранный ИЗ ПРОЧИТАННЫХ
    /// атрибутов S.M.A.R.T.
    /// </summary>
    /// <remarks>
    /// <para>Каждая строка ссылается на конкретный атрибут: 5 —
    /// перераспределённые секторы, 197 — ожидающие, 187 и 198 —
    /// неустранимые, 199 — ошибки интерфейса, 194 — температура,
    /// 9 — наработка. Если атрибута нет, строка не выводится вовсе.</para>
    ///
    /// <para>Раньше буква оценки и её цвет подставлялись по умолчанию даже
    /// без единого измеренного параметра, что читалось как «диск здоров»
    /// на накопителе, о котором ничего не известно.</para>
    /// </remarks>
    private void RenderSmartVerdict(StorageDisk disk)
    {
        if (SmartVerdictBorder == null) return;

        var attrs = disk.SmartAttributes;
        if (attrs.Count == 0)
        {
            SmartVerdictBorder.Visibility = Visibility.Collapsed;
            return;
        }

        SmartVerdictBorder.Visibility = Visibility.Visible;

        SmartAttribute? Find(int id) => attrs.FirstOrDefault(a => a.Id == id);

        var parts = new List<string>();

        // Итог самотеста — команда RETURN STATUS, отдельная от таблицы
        // атрибутов, поэтому может быть неизвестен даже при успешном чтении.
        if (disk.SmartOverallPass == true)
            parts.Add("Самотест пройден: накопитель не сообщает о неисправностях.");
        else if (disk.SmartOverallPass == false)
            parts.Add("ВНИМАНИЕ: самотест не пройден. Требуется замена накопителя.");

        // Перераспределённые секторы: главный признак износа носителя.
        var realloc = Find(5);
        if (realloc != null)
        {
            long v = realloc.RawValue & 0xFFFFFFFF;
            parts.Add(v == 0
                ? "Перераспределённых секторов нет, носитель не деградировал."
                : $"Перераспределено секторов: {v:N0}. Ненулевое значение означает износ.");
        }

        // Ожидающие секторы: система уже не может их прочитать.
        var pending = Find(197);
        if (pending != null)
        {
            long v = pending.RawValue & 0xFFFFFFFF;
            if (v > 0)
                parts.Add($"Секторов ожидают переприсвоения: {v:N0}, данные на них не читаются.");
        }

        // Неустранимые секторы: фактическая потеря данных.
        var unc = attrs.FirstOrDefault(a => a.Id is 187 or 198);
        if (unc != null)
        {
            long v = unc.RawValue & 0xFFFFFFFF;
            if (v > 0)
                parts.Add($"Некорректируемых секторов: {v:N0}. Возможна потеря данных.");
        }

        // Ошибки интерфейса указывают на кабель, а не на носитель.
        var crc = Find(199);
        if (crc != null)
        {
            long v = crc.RawValue & 0xFFFFFFFF;
            if (v > 0)
                parts.Add($"Ошибок интерфейса (CRC): {v:N0}. Проверьте кабель SATA или NVMe.");
        }

        if (disk.HasTemperature)
            parts.Add($"Температура {disk.TemperatureFormatted}.");
        if (disk.HasPowerOnHours)
            parts.Add($"Наработка {disk.PowerOnHoursFormatted}.");
        if (disk.HasWear)
            parts.Add($"Износ ресурса {disk.WearLevelPercent:F1}%.");

        SmartVerdictText.Text = parts.Count > 0
            ? string.Join("  ", parts)
            : "Атрибуты прочитаны, но ни один не относится к оценке состояния носителя.";

        // Откуда взяты данные — обязательно: пользователь должен видеть,
        // что значения прочитаны с накопителя, а не вычислены.
        var sources = new List<string>();
        if (disk.TemperatureSource.Length > 0) sources.Add(disk.TemperatureSource);
        if (disk.WearSource.Length > 0) sources.Add(disk.WearSource);
        SmartVerdictSourceText.Text = sources.Count > 0
            ? string.Join("  |  ", sources.Distinct())
            : "Источник: таблица S.M.A.R.T., прочитанная напрямую с накопителя.";
    }

    private void PopulateAdvisorRecommendations()
    {
        AdvisorRecommendationsStack.Children.Clear();
        var recs = StorageAdvisorService.GenerateRecommendations(_disks);

        if (recs.Count == 0)
        {
            AdvisorRecommendationsStack.Children.Add(new TextBlock
            {
                Text = "✔ Узких мест не обнаружено. Все накопители работают в оптимальном скоростном режиме.",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129)),
                Margin = new Thickness(0, 4, 0, 4)
            });
            return;
        }

        foreach (var r in recs)
        {
            var b = new Border
            {
                Background = (Brush)FindResource("WindowBackgroundBrush"),
                BorderBrush = (Brush)FindResource("GlassBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 6)
            };

            var dock = new DockPanel();

            // Кнопка действия
            if (!string.IsNullOrEmpty(r.ActionText))
            {
                var actionBtn = new Button
                {
                    Style = (Style)FindResource("CyberToolButton"),
                    Content = r.ActionText,
                    Padding = new Thickness(8, 4, 8, 4),
                    FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Center,
                    Tag = r
                };
                actionBtn.Click += RecommendationAction_Click;
                DockPanel.SetDock(actionBtn, Dock.Right);
                dock.Children.Add(actionBtn);
            }

            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var headerDock = new DockPanel();
            headerDock.Children.Add(new TextBlock { Text = $"{r.SeverityIcon} {r.Title}", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("PrimaryTextBrush") });
            textStack.Children.Add(headerDock);

            textStack.Children.Add(new TextBlock
            {
                Text = r.Description,
                FontSize = 10,
                Style = (Style)FindResource("MutedText"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 2)
            });

            if (!string.IsNullOrEmpty(r.EstimatedBenefit))
            {
                textStack.Children.Add(new TextBlock
                {
                    Text = $"Эффект: {r.EstimatedBenefit}",
                    FontSize = 9,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("AccentBrush")
                });
            }

            dock.Children.Add(textStack);
            b.Child = dock;
            AdvisorRecommendationsStack.Children.Add(b);
        }
    }

    private void RecommendationAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is StorageRecommendation rec)
        {
            if (rec.ActionCommand == "Cleanup")
            {
                SubTabCleanupRadio.IsChecked = true;
            }
            else if (rec.ActionCommand is "Trim" or "Defrag")
            {
                SubTabOptimizerRadio.IsChecked = true;
            }
        }
    }

    private void RenderPartitionMap(StorageDisk disk)
    {
        PartitionMapGrid.ColumnDefinitions.Clear();
        PartitionMapGrid.Children.Clear();
        PartitionMapStyleText.Text = $"Стиль разметки: {disk.PartitionStyle} ({disk.Partitions.Count} томов)";

        if (disk.Partitions.Count == 0) return;

        int colIdx = 0;
        foreach (var p in disk.Partitions)
        {
            double weight = Math.Max(0.08, (double)p.SizeBytes / disk.TotalSizeBytes);
            PartitionMapGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(weight, GridUnitType.Star) });

            var block = new Border
            {
                Margin = new Thickness(colIdx > 0 ? 2 : 0, 0, 0, 0),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4, 2, 4, 2),
                Cursor = Cursors.Hand,
                Tag = p
            };

            // Глянцевые градиенты для карты разделов (Zero-Conflict Style)
            block.Background = p.Category switch
            {
                PartitionTypeCategory.SystemEfi => (Brush)FindResource("PartitionSystemEfiGradient"),
                PartitionTypeCategory.MicrosoftReserved => (Brush)FindResource("PartitionMsrGradient"),
                PartitionTypeCategory.Recovery => (Brush)FindResource("PartitionRecoveryGradient"),
                PartitionTypeCategory.Unallocated => (Brush)FindResource("PartitionUnallocatedGradient"),
                _ => (Brush)FindResource("PartitionBasicDataGradient")
            };

            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            string letterStr = !string.IsNullOrEmpty(p.DriveLetter) ? $"{p.DriveLetter}:" : (p.Category == PartitionTypeCategory.Unallocated ? "Свободно" : p.DisplayName);
            stack.Children.Add(new TextBlock
            {
                Text = letterStr,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            stack.Children.Add(new TextBlock
            {
                Text = p.SizeFormatted,
                FontSize = 8,
                Foreground = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center
            });

            block.Child = stack;
            block.MouseLeftButtonUp += (s, e) => SelectPartition(p);

            Grid.SetColumn(block, colIdx);
            PartitionMapGrid.Children.Add(block);
            colIdx++;
        }

        PartitionsListView.ItemsSource = disk.Partitions;
        PartitionLogsList.ItemsSource = PartitionManagementService.OperationLogs;

        if (disk.Partitions.Count > 0)
        {
            SelectPartition(disk.Partitions[0]);
        }
    }

    private void SelectPartition(StoragePartition p)
    {
        _selectedPartition = p;
        PartitionsListView.SelectedItem = p;

        string letter = string.IsNullOrEmpty(p.DriveLetter) ? "" : $"{p.DriveLetter}: ";
        SelectedPartitionTitleText.Text = $"Выбран: {letter}{p.DisplayName} ({p.SizeFormatted})";
        SelectedPartitionDetailsText.Text = $"Файловая система: {p.FileSystem} • Свободно: {p.FreeSpaceFormatted} • Тип: {p.Category} {(p.IsSystem ? "• СИСТЕМНЫЙ РАЗДЕЛ (Защищен)" : "")}";

        bool isProtected = p.IsSystem || p.IsBoot || p.DriveLetter.Equals("C", StringComparison.OrdinalIgnoreCase);
        bool isUnallocated = p.Category == PartitionTypeCategory.Unallocated;

        ProtectedBadgeBorder.Visibility = isProtected ? Visibility.Visible : Visibility.Collapsed;

        // Обновление 4 плиток Hero Card (Zero-Conflict Layout)
        HeroVolumeText.Text = string.IsNullOrEmpty(p.DriveLetter) ? (isUnallocated ? "Не распределено" : p.DisplayName) : $"[{p.DriveLetter}:] {p.VolumeLabel}";
        HeroFsText.Text = string.IsNullOrEmpty(p.FileSystem) ? p.Category.ToString() : $"{p.FileSystem} • {p.Category}";
        HeroCapacityText.Text = $"{p.SizeFormatted} ({p.FreeSpaceFormatted} своб.)";
        HeroSecurityText.Text = isProtected ? "🛡 СИСТЕМНЫЙ ТОМ (Защищен)" : (isUnallocated ? "⚪ Не размечено" : "🟢 Доступен для разметки");
        HeroSecurityText.Foreground = isProtected ? new SolidColorBrush(Color.FromRgb(239, 68, 68)) : (isUnallocated ? new SolidColorBrush(Color.FromRgb(148, 163, 184)) : new SolidColorBrush(Color.FromRgb(16, 185, 129)));

        PartDeleteBtn.IsEnabled = !isProtected && !isUnallocated;
        PartFormatBtn.IsEnabled = !isProtected && !isUnallocated;
        PartShrinkBtn.IsEnabled = !isProtected && !isUnallocated;
        PartExtendBtn.IsEnabled = !isProtected && !isUnallocated;
        PartChangeLetterBtn.IsEnabled = !isProtected && !isUnallocated;
        PartChangeLabelBtn.IsEnabled = !isProtected && !isUnallocated;
        PartChkdskBtn.IsEnabled = !string.IsNullOrEmpty(p.DriveLetter);
        PartCreateBtn.IsEnabled = isUnallocated || (_selectedDisk != null && _selectedDisk.UnallocatedSizeBytes > 0);

        foreach (var child in PartitionMapGrid.Children)
        {
            if (child is Border b)
            {
                if (b.Tag == p)
                {
                    b.BorderBrush = (Brush)FindResource("AccentBrush");
                    b.BorderThickness = new Thickness(2);
                    b.Effect = new DropShadowEffect
                    {
                        BlurRadius = 12,
                        ShadowDepth = 0,
                        Color = Color.FromRgb(0, 240, 255),
                        Opacity = 0.85
                    };
                }
                else
                {
                    b.BorderBrush = (Brush)FindResource("GlassBorderBrush");
                    b.BorderThickness = new Thickness(1);
                    b.Effect = null;
                }
            }
        }
    }

    private void PartitionsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PartitionsListView.SelectedItem is StoragePartition p)
        {
            SelectPartition(p);
        }
    }

    private void SubTab_Checked(object sender, RoutedEventArgs e)
    {
        if (ViewHealth == null || ViewPartitions == null || ViewBenchmark == null || ViewOptimizer == null || ViewCleanup == null || ViewSafety == null || ViewMigration == null)
            return;

        if (sender == SubTabHealthRadio)
        {
            SubTabHealthRadio.IsChecked = true;
            SubTabPartitionsRadio.IsChecked = false;
            SubTabBenchmarkRadio.IsChecked = false;
            SubTabOptimizerRadio.IsChecked = false;
            SubTabCleanupRadio.IsChecked = false;
            SubTabSafetyRadio.IsChecked = false;
            SubTabMigrationRadio.IsChecked = false;
        }
        else if (sender == SubTabPartitionsRadio)
        {
            SubTabHealthRadio.IsChecked = false;
            SubTabPartitionsRadio.IsChecked = true;
            SubTabBenchmarkRadio.IsChecked = false;
            SubTabOptimizerRadio.IsChecked = false;
            SubTabCleanupRadio.IsChecked = false;
            SubTabSafetyRadio.IsChecked = false;
        }
        else if (sender == SubTabBenchmarkRadio)
        {
            SubTabHealthRadio.IsChecked = false;
            SubTabPartitionsRadio.IsChecked = false;
            SubTabBenchmarkRadio.IsChecked = true;
            SubTabOptimizerRadio.IsChecked = false;
            SubTabCleanupRadio.IsChecked = false;
            SubTabSafetyRadio.IsChecked = false;
        }
        else if (sender == SubTabOptimizerRadio)
        {
            SubTabHealthRadio.IsChecked = false;
            SubTabPartitionsRadio.IsChecked = false;
            SubTabBenchmarkRadio.IsChecked = false;
            SubTabOptimizerRadio.IsChecked = true;
            SubTabCleanupRadio.IsChecked = false;
            SubTabSafetyRadio.IsChecked = false;
        }
        else if (sender == SubTabCleanupRadio)
        {
            SubTabHealthRadio.IsChecked = false;
            SubTabPartitionsRadio.IsChecked = false;
            SubTabBenchmarkRadio.IsChecked = false;
            SubTabOptimizerRadio.IsChecked = false;
            SubTabCleanupRadio.IsChecked = true;
            SubTabSafetyRadio.IsChecked = false;
        }
        else if (sender == SubTabSafetyRadio)
        {
            SubTabHealthRadio.IsChecked = false;
            SubTabPartitionsRadio.IsChecked = false;
            SubTabBenchmarkRadio.IsChecked = false;
            SubTabOptimizerRadio.IsChecked = false;
            SubTabCleanupRadio.IsChecked = false;
            SubTabSafetyRadio.IsChecked = true;
            SubTabMigrationRadio.IsChecked = false;
        }
        else if (sender == SubTabMigrationRadio)
        {
            SubTabHealthRadio.IsChecked = false;
            SubTabPartitionsRadio.IsChecked = false;
            SubTabBenchmarkRadio.IsChecked = false;
            SubTabOptimizerRadio.IsChecked = false;
            SubTabCleanupRadio.IsChecked = false;
            SubTabSafetyRadio.IsChecked = false;
            SubTabMigrationRadio.IsChecked = true;
        }

        ViewHealth.Visibility = SubTabHealthRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ViewPartitions.Visibility = SubTabPartitionsRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ViewBenchmark.Visibility = SubTabBenchmarkRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ViewOptimizer.Visibility = SubTabOptimizerRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ViewCleanup.Visibility = SubTabCleanupRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ViewSafety.Visibility = SubTabSafetyRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ViewMigration.Visibility = SubTabMigrationRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void RefreshDrives_Click(object sender, RoutedEventArgs e)
    {
        // Раньше исключение из RefreshDisksAsync уходило в глобальный обработчик,
        // а кнопка оставалась заблокированной навсегда.
        RefreshDrivesBtn.IsEnabled = false;
        try
        {
            await RefreshDisksAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось обновить список накопителей: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            // UI восстанавливается в finally — кнопка не должна остаться
            // заблокированной ни при какой ошибке.
            RefreshDrivesBtn.IsEnabled = true;
        }
    }

    private void ExportReport_Click(object sender, RoutedEventArgs e)
    {
        if (_disks.Count == 0) return;

        string report = StorageReportService.GenerateTextReport(_disks);
        string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        string file = Path.Combine(desktopPath, $"Storage_Report_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        File.WriteAllText(file, report);

        MessageBox.Show($"Диагностический отчет Storage Control Center успешно сохранен на рабочий стол:\n\n{file}", "Отчет сохранен", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ================= БЕНЧМАРК =================
    private async void StartBenchmark_Click(object sender, RoutedEventArgs e)
    {
        // Гонка (была): обработчик «стоп» делал _benchCts.Cancel(); _benchCts = null;
        // затем можно было начать НОВЫЙ тест, а finally СТАРОГО теста
        // выполнял _benchCts = null и стирал токен нового. Кнопка показывала
        // «ЗАПУСТИТЬ», а идущий тест уже нельзя было отменить.
        // Теперь поле обнуляет только владелец и только если поле всё ещё
        // указывает на ЕГО источник (Interlocked.CompareExchange).
        if (Volatile.Read(ref _benchCts) != null)
        {
            // Стоп только просит об отмене. Никаких Dispose()/= null здесь:
            // источник ещё использует фоновая задача, а поле чистит
            // finally владельца. Пока он не обнулён, повторное нажатие
            // просто повторяет отмену, а не запускает второй тест.
            SafeCancel(Volatile.Read(ref _benchCts));
            StartBenchBtn.Content = "⏹ ОСТАНОВКА...";
            BenchProgressStatusText.Text = "Остановка теста...";
            return;
        }

        string target = BenchTargetDriveCombo.SelectedItem?.ToString() ?? "C:\\";
        int sizeBytes = BenchSizeCombo.SelectedIndex switch
        {
            0 => 64 * 1024 * 1024,
            2 => 512 * 1024 * 1024,
            3 => 1024 * 1024 * 1024,
            _ => 256 * 1024 * 1024
        };

        string sizeFormatted = Helpers.Formatters.Bytes(sizeBytes);
        var confirm = MessageBox.Show(
            $"⚠ ВНИМАНИЕ: Запуск аппаратного бенчмарка скорости\n\n" +
            $"• Целевой накопитель: {target}\n" +
            $"• Будет записан временный тестовый файл размером {sizeFormatted}\n" +
            $"• Накопитель будет кратковременно нагружен на 100% линейными и случайными операциями\n" +
            $"• Длительность тестирования: ~15–30 секунд\n" +
            $"• Все временные данные будут автоматически удалены сразу после завершения\n\n" +
            $"Продолжить тестирование?",
            "Подтверждение бенчмарка",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        var config = new BenchmarkConfig
        {
            TargetDrive = target,
            FileSizeBytes = sizeBytes
        };

        // Свой экземпляр CTS: операция работает только с ним, поле — лишь
        // «объявление» о том, что тест идёт (для кнопки стоп и Unloaded).
        var myCts = new CancellationTokenSource();
        Interlocked.Exchange(ref _benchCts, myCts);

        StartBenchBtn.Content = "⏹ ОСТАНОВИТЬ ТЕСТ";
        BenchProgressCard.Visibility = Visibility.Visible;
        BenchProgressBar.Value = 0;

        var progress = new Progress<(string testName, int percent, double currentSpeed)>(p =>
        {
            BenchProgressStatusText.Text = p.testName;
            BenchProgressBar.SetSafe(p.percent);
            BenchLiveSpeedText.Text = $"{p.currentSpeed:F1} МБ/с";
        });

        try
        {
            var res = await StorageBenchmarkService.RunBenchmarkAsync(config, progress, myCts.Token);
            BenchmarkResultsList.ItemsSource = res.Items;
            BenchScoreSummaryText.Text = $"Общий рейтинг: {res.OverallPerformanceScore:F0} баллов";
            if (BenchTempCleanedText != null) BenchTempCleanedText.Text = res.TempCleanupStatus;
        }
        catch (OperationCanceledException)
        {
            BenchProgressStatusText.Text = "Тест остановлен пользователем";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка выполнения бенчмарка: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SafeCancel(myCts);

            // Возвращаем поле в null только если там всё ещё наш источник:
            // иначе (теоретически) стёрли бы токен следующего теста.
            bool isCurrent = Interlocked.CompareExchange(ref _benchCts, null, myCts) == myCts;

            if (isCurrent)
            {
                StartBenchBtn.Content = "⚡ ЗАПУСТИТЬ ТЕСТ СКОРОСТИ";
                BenchProgressCard.Visibility = Visibility.Collapsed;
            }

            // Освобождает ТОЛЬКО владелец источника.
            myCts.Dispose();
        }
    }

    // ================= ОПТИМИЗАТОР =================
    private async void RunOptimize_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDisk == null) return;

        string targetLetter = _selectedDisk.Partitions.FirstOrDefault(p => !string.IsNullOrEmpty(p.DriveLetter))?.DriveLetter ?? "C";
        string mode = (OptimizerModeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Smart";

        RunOptimizeBtn.IsEnabled = false;
        OptimizerStatusText.Text = "Выполняется оптимизация...";
        OptimizerLogText.Text = $"[{DateTime.Now:HH:mm:ss}] Запуск оптимизации тома {targetLetter}:...\n";

        var progress = new Progress<string>(msg =>
        {
            OptimizerLogText.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\n");
            OptimizerLogText.ScrollToEnd();
        });

        try
        {
            var (success, output) = await DiskOptimizerService.OptimizeDriveAsync(_selectedDisk, targetLetter, mode, progress);
            OptimizerStatusText.Text = success ? "Оптимизация завершена" : "Завершено с предупреждением";
        }
        catch (Exception ex)
        {
            OptimizerLogText.AppendText($"[ОШИБКА] {ex.Message}\n");
        }
        finally
        {
            RunOptimizeBtn.IsEnabled = true;
        }
    }

    // ================= ОЧИСТКА =================
    private async void LoadCleanupCategories()
    {
        // Этот метод вызывается и из RefreshDisksAsync, и из CleanNow_Click.
        // Раньше он был «голым» await: исключение из сканирования всплывало
        // в глобальный обработчик и подменялось модальным окном ошибки,
        // хотя это фоновые справочные данные.
        try
        {
            CleanupTotalReclaimableText.Text = "Сканирование временных файлов...";
            var items = await StorageCleanupService.ScanCleanupCategoriesAsync();
            CleanupCategoriesList.ItemsSource = items;

            long total = items.Sum(i => i.SizeBytes);
            CleanupTotalReclaimableText.Text = $"Найдено для очистки: {Formatters.Bytes(total)}";
        }
        catch (Exception ex)
        {
            CleanupCategoriesList.ItemsSource = null;
            CleanupTotalReclaimableText.Text = $"Не удалось просканировать временные файлы: {ex.Message}";
        }
    }

    private async void CleanNow_Click(object sender, RoutedEventArgs e)
    {
        if (CleanupCategoriesList.ItemsSource is not IEnumerable<StorageCleanupItem> items) return;

        CleanNowBtn.IsEnabled = false;
        try
        {
            var (cleanedBytes, deletedFiles) = await StorageCleanupService.CleanSelectedAsync(items);

            MessageBox.Show($"Очистка успешно завершена!\n\nОсвобождено места: {Formatters.Bytes(cleanedBytes)}\nУдалено временных файлов: {deletedFiles}", "Очистка кэша", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            // Раньше исключение из очистки уходило вверх: кнопка навсегда
            // оставалась в состоянии «выполняется», а пользователь видел
            // глобальное окно ошибки без указания, что именно очистка упала.
            MessageBox.Show($"Очистка временных файлов прервана ошибкой: {ex.Message}", "Ошибка очистки", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            // Кнопка разблокируется ЛЮБЫМ исходом — иначе повторная очистка
            // была бы невозможна до перезапуска приложения.
            CleanNowBtn.IsEnabled = true;
        }

        LoadCleanupCategories();
    }

    private async void ScanLargeFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDisk == null) return;

        string targetLetter = _selectedDisk.Partitions.FirstOrDefault(p => !string.IsNullOrEmpty(p.DriveLetter))?.DriveLetter ?? "C";
        string root = $"{targetLetter}:\\";

        ScanLargeFilesBtn.IsEnabled = false;
        try
        {
            var (files, _) = await StorageExplorerService.AnalyzeStorageUsageAsync(root);
            LargeFilesListView.ItemsSource = files;
        }
        catch (Exception ex)
        {
            // Раньше был только finally: исключение уходило в глобальный
            // обработчик (модальное окно), хотя состояние кнопки было в порядке.
            LargeFilesListView.ItemsSource = null;
            MessageBox.Show($"Сканирование крупных файлов на {root} прервано ошибкой: {ex.Message}", "Ошибка сканирования", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            // finally, а не happy path: любая ошибка тоже обязана вернуть кнопку.
            ScanLargeFilesBtn.IsEnabled = true;
        }
    }

    // ================= WIPE =================
    private async void StartWipe_Click(object sender, RoutedEventArgs e)
    {
        // Ровно та же гонка, что и в бенчмарке: стоп обнулял поле, новый запуск
        // создавал новый CTS, а finally старой операции стирал его — wipe
        // продолжал работать, но уже не отменялся, а кнопка врала «ЗАПУСТИТЬ».
        if (Volatile.Read(ref _wipeCts) != null)
        {
            // Только отмена. Dispose()/обнуление — дело владельца (finally).
            SafeCancel(Volatile.Read(ref _wipeCts));
            StartWipeBtn.Content = "⏹ ОСТАНОВКА...";
            WipeStatusText.Text = "Остановка очистки...";
            return;
        }

        if (_selectedDisk == null) return;
        string targetLetter = _selectedDisk.Partitions.FirstOrDefault(p => !string.IsNullOrEmpty(p.DriveLetter))?.DriveLetter ?? "D";

        var confirm = MessageBox.Show(
            $"Вы действительно хотите очистить удаленные данные на свободном пространстве тома {targetLetter}:?\n\nСуществующие файлы НЕ будут затронуты. Удаленные секторы будут перезаписаны нулями.",
            "Подтверждение Wipe",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        // Свой экземпляр CTS: поле — только признак «wipe идёт».
        var myCts = new CancellationTokenSource();
        Interlocked.Exchange(ref _wipeCts, myCts);

        StartWipeBtn.Content = "⏹ ОСТАНОВИТЬ WIPE";
        WipeProgressBorder.Visibility = Visibility.Visible;
        WipeProgressBar.Value = 0;

        var progress = new Progress<(int percent, string status, double speedMBps)>(p =>
        {
            WipeProgressBar.SetSafe(p.percent);
            WipeStatusText.Text = p.status;
            WipeSpeedText.Text = $"{p.speedMBps:F1} МБ/с";
        });

        try
        {
            var (success, msg) = await DiskWipeService.WipeFreeSpaceAsync(targetLetter, progress, myCts.Token);
            MessageBox.Show(msg, "Очистка свободного места", MessageBoxButton.OK, success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (OperationCanceledException)
        {
            WipeStatusText.Text = "Операция отменена";
        }
        catch (Exception ex)
        {
            // Раньше любое исключение из DiskWipeService уходило в глобальный
            // обработчик, пока кнопка оставалась в состоянии «идёт операция».
            WipeStatusText.Text = $"Ошибка очистки: {ex.Message}";
            MessageBox.Show($"Очистка свободного места прервана ошибкой: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SafeCancel(myCts);

            // Поле обнуляет только владелец и только если поле всё ещё его.
            bool isCurrent = Interlocked.CompareExchange(ref _wipeCts, null, myCts) == myCts;

            if (isCurrent)
            {
                StartWipeBtn.Content = "🛡 ЗАПУСТИТЬ ОЧИСТКУ СВОБОДНОГО МЕСТА";
                WipeProgressBorder.Visibility = Visibility.Collapsed;
            }

            // Освобождает ТОЛЬКО владелец источника.
            myCts.Dispose();
        }
    }

    // ================= DISK PARTITION MANAGER: ДЕЙСТВИЯ С РАЗДЕЛАМИ =================

    private void PartCreate_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDisk == null) return;

        long defaultMb = 10240;
        if (_selectedPartition != null && _selectedPartition.Category == PartitionTypeCategory.Unallocated && _selectedPartition.SizeBytes > 0)
        {
            defaultMb = _selectedPartition.SizeBytes / (1024 * 1024);
        }
        else
        {
            long unalloc = _selectedDisk.UnallocatedSizeBytes;
            if (unalloc > 0) defaultMb = unalloc / (1024 * 1024);
        }
        if (defaultMb <= 0) defaultMb = 10240;

        _currentAction = "Create";
        ActionBoxTitleText.Text = $"➕ Создать новый раздел на накопителе Диск {_selectedDisk.DiskNumber} ({_selectedDisk.Model})";
        ActionBoxExecuteBtn.Content = "✔ СОЗДАТЬ И РАЗМЕТИТЬ ТОМ";
        ActionFormContainer.Children.Clear();

        // 1. Размер раздела в МБ
        _inputSizeMb = CreateStyledTextBox(defaultMb.ToString());
        ActionFormContainer.Children.Add(CreateFormRow("Размер раздела (МБ):", _inputSizeMb, $"Максимум доступно: {defaultMb} МБ"));

        // 2. Файловая система
        _comboFs = CreateStyledComboBox(new[] { "NTFS", "exFAT", "FAT32" }, "NTFS");
        ActionFormContainer.Children.Add(CreateFormRow("Файловая система:", _comboFs, "Для Windows и системных файлов рекомендуется NTFS"));

        // 3. Свободная буква диска
        var availableLetters = GetAvailableDriveLetters();
        var letterOptions = availableLetters.Select(c => $"{c}:").ToList();
        letterOptions.Insert(0, "[Без буквы]");
        _comboLetter = CreateStyledComboBox(letterOptions, letterOptions.Count > 1 ? letterOptions[1] : letterOptions[0]);
        ActionFormContainer.Children.Add(CreateFormRow("Буква диска:", _comboLetter, "Доступные незанятые буквы в системе"));

        // 4. Метка тома
        _inputLabel = CreateStyledTextBox("Новый том");
        ActionFormContainer.Children.Add(CreateFormRow("Метка тома:", _inputLabel, "Имя, отображаемое в проводнике Windows"));

        // 5. Размер кластера
        _comboCluster = CreateStyledComboBox(new[] { "4096 (По умолчанию)", "8192", "16384", "65536" }, "4096 (По умолчанию)");
        ActionFormContainer.Children.Add(CreateFormRow("Размер кластера:", _comboCluster, "Стандартный сектор: 4 КБ"));

        PartitionActionBox.Visibility = Visibility.Visible;
    }

    private async void PartDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPartition == null)
        {
            MessageBox.Show("Пожалуйста, выберите раздел на карте диска для удаления.", "Выбор раздела", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_selectedPartition.IsSystem || _selectedPartition.DriveLetter.Equals("C", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("ЗАЩИТА WINDOWS: Удаление системного раздела (C:) категорически заблокировано во избежание сбоя системы.", "Заблокировано", MessageBoxButton.OK, MessageBoxImage.Stop);
            return;
        }

        var res = MessageBox.Show(
            $"КРИТИЧЕСКОЕ ПРЕДУПРЕЖДЕНИЕ: Вы действительно хотите удалить раздел {_selectedPartition.DisplayName}?\n\n" +
            $"Диск: {_selectedPartition.DiskNumber}\nРаздел: #{_selectedPartition.PartitionNumber}\nОбъем: {_selectedPartition.SizeFormatted}\n\n" +
            $"Все хранящиеся файлы будут безвозвратно стерты, а пространство станет нераспределенным.\n" +
            $"Команда будет выполнена с SuperAdmin флагом OVERRIDE (принудительное снятие блокировок Windows).",
            "SuperAdmin Force Delete",
            MessageBoxButton.YesNo,
            MessageBoxImage.Stop);

        if (res == MessageBoxResult.Yes)
        {
            bool success;
            string msg;
            try
            {
                (success, msg) = await PartitionManagementService.DeletePartitionAsync(_selectedPartition, forceOverride: true);
            }
            catch (Exception ex)
            {
                // Необратимая операция — раньше любое исключение было фатальным.
                success = false;
                msg = $"Удаление раздела прервано ошибкой: {ex.Message}";
            }

            MessageBox.Show(msg, "Удаление раздела", MessageBoxButton.OK, success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            PartitionActionBox.Visibility = Visibility.Collapsed;
            await RefreshDisksAsync();
        }
    }

    private void PartShrink_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPartition == null)
        {
            MessageBox.Show("Пожалуйста, выберите раздел на карте диска для сжатия.", "Выбор раздела", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_selectedPartition.IsSystem || _selectedPartition.DriveLetter.Equals("C", StringComparison.OrdinalIgnoreCase))
        {
            // Раньше здесь отсутствовал return: после предупреждения выполнение
            // проваливалось и форма сжатия всё равно показывалась для тома C:.
            MessageBox.Show("Сжатие системного тома C: ограничено политиками безопасности Windows во время активной сессии.", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        long defaultShrinkMb = 5120;
        if (_selectedPartition.FreeSpaceBytes > 0)
        {
            defaultShrinkMb = Math.Min(_selectedPartition.FreeSpaceBytes / (1024 * 1024) / 2, 51200);
            if (defaultShrinkMb <= 0) defaultShrinkMb = 1024;
        }

        _currentAction = "Shrink";
        ActionBoxTitleText.Text = $"↔ Сжатие тома {_selectedPartition.DisplayName} (Текущий размер: {_selectedPartition.SizeFormatted})";
        ActionBoxExecuteBtn.Content = "✔ ВЫПОЛНИТЬ СЖАТИЕ ТОМА";
        ActionFormContainer.Children.Clear();

        _inputSizeMb = CreateStyledTextBox(defaultShrinkMb.ToString());
        ActionFormContainer.Children.Add(CreateFormRow("Уменьшить объем на (МБ):", _inputSizeMb, $"Свободно на томе: {_selectedPartition.FreeSpaceFormatted}. Высвобожденное место станет нераспределенным."));

        PartitionActionBox.Visibility = Visibility.Visible;
    }

    private void PartExtend_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPartition == null)
        {
            MessageBox.Show("Пожалуйста, выберите раздел на карте диска для расширения.", "Выбор раздела", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _currentAction = "Extend";
        ActionBoxTitleText.Text = $"➡ Расширение тома {_selectedPartition.DisplayName} (Текущий размер: {_selectedPartition.SizeFormatted})";
        ActionBoxExecuteBtn.Content = "✔ РАСШИРИТЬ ТОМ";
        ActionFormContainer.Children.Clear();

        _inputSizeMb = CreateStyledTextBox("0");
        ActionFormContainer.Children.Add(CreateFormRow("Добавить объем (МБ):", _inputSizeMb, "Укажите '0' для расширения на всё смежное нераспределенное пространство"));

        PartitionActionBox.Visibility = Visibility.Visible;
    }

    private void PartFormat_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPartition == null)
        {
            MessageBox.Show("Пожалуйста, выберите раздел для форматирования.", "Выбор раздела", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_selectedPartition.IsSystem || _selectedPartition.DriveLetter.Equals("C", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("ЗАЩИТА WINDOWS: Форматирование системного тома (C:) категорически запрещено во избежание краха ОС.", "Заблокировано", MessageBoxButton.OK, MessageBoxImage.Stop);
            return;
        }

        _currentAction = "Format";
        ActionBoxTitleText.Text = $"💾 Форматирование тома {_selectedPartition.DisplayName} ({_selectedPartition.SizeFormatted})";
        ActionBoxExecuteBtn.Content = "✔ ВЫПОЛНИТЬ ФОРМАТИРОВАНИЕ";
        ActionFormContainer.Children.Clear();

        _comboFs = CreateStyledComboBox(new[] { "NTFS", "exFAT", "FAT32" }, _selectedPartition.FileSystem.Contains("FAT") ? "exFAT" : "NTFS");
        ActionFormContainer.Children.Add(CreateFormRow("Файловая система:", _comboFs, "NTFS для локальных дисков, exFAT для переносимых накопителей"));

        _inputLabel = CreateStyledTextBox(string.IsNullOrEmpty(_selectedPartition.VolumeLabel) ? "Локальный диск" : _selectedPartition.VolumeLabel);
        ActionFormContainer.Children.Add(CreateFormRow("Метка тома:", _inputLabel, "Имя диска в проводнике"));

        _comboCluster = CreateStyledComboBox(new[] { "4096", "8192", "16384", "65536" }, "4096");
        ActionFormContainer.Children.Add(CreateFormRow("Размер кластера (байт):", _comboCluster, "4096 байт стандартно для Windows"));

        _chkQuick = new CheckBox
        {
            Content = "Быстрое форматирование (очистка таблицы файлов без глубокого посекторного сканирования)",
            IsChecked = true,
            Foreground = (Brush)FindResource("PrimaryTextBrush"),
            Margin = new Thickness(0, 4, 0, 4)
        };
        ActionFormContainer.Children.Add(_chkQuick);

        PartitionActionBox.Visibility = Visibility.Visible;
    }

    private void PartChangeLetter_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPartition == null)
        {
            MessageBox.Show("Пожалуйста, выберите раздел для назначения/смены буквы.", "Выбор раздела", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_selectedPartition.IsSystem || _selectedPartition.DriveLetter.Equals("C", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("ЗАЩИТА WINDOWS: Смена буквы системного диска C: запрещена, так как это нарушит работу ОС и службы.", "Заблокировано", MessageBoxButton.OK, MessageBoxImage.Stop);
            return;
        }

        _currentAction = "ChangeLetter";
        ActionBoxTitleText.Text = $"🔤 Назначение / Смена буквы диска для {_selectedPartition.DisplayName}";
        ActionBoxExecuteBtn.Content = "✔ ПРИМЕНИТЬ БУКВУ";
        ActionFormContainer.Children.Clear();

        var availableLetters = GetAvailableDriveLetters();
        var options = availableLetters.Select(c => $"{c}:").ToList();
        options.Insert(0, "[Удалить букву (Скрыть раздел)]");

        string currentTarget = string.IsNullOrEmpty(_selectedPartition.DriveLetter) ? options[0] : $"{_selectedPartition.DriveLetter}:";
        _comboLetter = CreateStyledComboBox(options, options.Contains(currentTarget) ? currentTarget : options[0]);
        ActionFormContainer.Children.Add(CreateFormRow("Новая буква диска:", _comboLetter, "Буква будет немедленно смонтирована в Проводнике Windows"));

        PartitionActionBox.Visibility = Visibility.Visible;
    }

    private void PartChangeLabel_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPartition == null)
        {
            MessageBox.Show("Пожалуйста, выберите раздел для смены метки.", "Выбор раздела", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _currentAction = "ChangeLabel";
        ActionBoxTitleText.Text = $"🏷 Изменение метки тома для {_selectedPartition.DisplayName}";
        ActionBoxExecuteBtn.Content = "✔ СОХРАНИТЬ МЕТКУ";
        ActionFormContainer.Children.Clear();

        _inputLabel = CreateStyledTextBox(_selectedPartition.VolumeLabel ?? "Данные");
        ActionFormContainer.Children.Add(CreateFormRow("Новая метка тома:", _inputLabel, "Отображаемое название раздела"));

        PartitionActionBox.Visibility = Visibility.Visible;
    }

    private async void DiskClean_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDisk == null) return;

        bool containsC = _selectedDisk.Partitions.Any(p => p.DriveLetter.Equals("C", StringComparison.OrdinalIgnoreCase) || p.IsBoot || p.IsSystem);
        if (containsC)
        {
            MessageBox.Show("ЗАЩИТА WINDOWS: Очистка всего диска (Clean) на системном накопителе с ОС Windows (C:) категорически заблокирована!", "Критическая защита", MessageBoxButton.OK, MessageBoxImage.Stop);
            return;
        }

        var res = MessageBox.Show(
            $"ВНИМАНИЕ! Вы запускаете полную очистку диска (DiskPart CLEAN) для накопителя:\n\n" +
            $"Диск: #{_selectedDisk.DiskNumber} — {_selectedDisk.Model} ({_selectedDisk.TotalSizeFormatted})\n\n" +
            $"ВСЕ существующие разделы, таблицы разметки MBR/GPT и данные на этом диске будут БЕЗВОЗВРАТНО СТЕРТЫ!\n" +
            $"Диск вернется в исходное неинициализированное состояние.\n\n" +
            $"Вы подтверждаете выполнение операции?",
            "DiskPart Full Clean",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (res == MessageBoxResult.Yes)
        {
            bool success;
            string msg;
            try
            {
                (success, msg) = await PartitionManagementService.CleanDiskAsync(_selectedDisk);
            }
            catch (Exception ex)
            {
                // Дискpart/WMI бросают исключения регулярно. Без catch это
                // фатальный краш посреди необратимой операции очистки диска.
                success = false;
                msg = $"Очистка диска прервана ошибкой: {ex.Message}";
            }

            MessageBox.Show(msg, "Очистка диска", MessageBoxButton.OK, success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            PartitionActionBox.Visibility = Visibility.Collapsed;
            await RefreshDisksAsync();
        }
    }

    private async void DiskClearReadOnly_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDisk == null) return;

        int? partNum = _selectedPartition?.PartitionNumber;

        try
        {
            var (success, msg) = await PartitionManagementService.ClearReadOnlyAsync(_selectedDisk.DiskNumber, partNum);
            MessageBox.Show(msg, "Снятие защиты от записи", MessageBoxButton.OK, success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось снять защиту от записи: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void PartChkdsk_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPartition == null || string.IsNullOrEmpty(_selectedPartition.DriveLetter))
        {
            MessageBox.Show("Для запуска Chkdsk выберите том, имеющий назначенную букву диска.", "Проверка Chkdsk", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var (success, msg) = await PartitionManagementService.CheckFileSystemAsync(_selectedPartition.DriveLetter);
            MessageBox.Show(msg, $"Chkdsk: {_selectedPartition.DriveLetter}:", MessageBoxButton.OK, success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Проверка Chkdsk прервана ошибкой: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ActionBoxCancel_Click(object sender, RoutedEventArgs e)
    {
        PartitionActionBox.Visibility = Visibility.Collapsed;
    }

    private async void ActionBoxExecute_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDisk == null) return;

        ActionBoxExecuteBtn.IsEnabled = false;
        try
        {
            switch (_currentAction)
            {
                case "Create":
                    {
                        if (!long.TryParse(_inputSizeMb?.Text?.Trim(), out long sizeMb) || sizeMb <= 0)
                        {
                            MessageBox.Show("Введите корректный размер раздела в МБ.", "Ошибка ввода", MessageBoxButton.OK, MessageBoxImage.Warning);
                            return;
                        }

                        string fs = (_comboFs?.SelectedItem as string) ?? "NTFS";
                        string label = string.IsNullOrWhiteSpace(_inputLabel?.Text) ? "Новый том" : _inputLabel.Text.Trim();

                        char? letter = null;
                        if (_comboLetter?.SelectedItem is string letterStr && letterStr.Length >= 2 && letterStr[1] == ':')
                        {
                            letter = letterStr[0];
                        }

                        int cluster = 4096;
                        if (_comboCluster?.SelectedItem is string clusterStr)
                        {
                            string numOnly = new string(clusterStr.TakeWhile(char.IsDigit).ToArray());
                            if (int.TryParse(numOnly, out int cVal)) cluster = cVal;
                        }

                        long sizeBytes = sizeMb * 1024L * 1024L;
                        var (ok, resMsg) = await PartitionManagementService.CreatePartitionAsync(_selectedDisk.DiskNumber, sizeBytes, fs, label, letter, cluster);
                        MessageBox.Show(resMsg, "Создание раздела", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
                        if (ok) PartitionActionBox.Visibility = Visibility.Collapsed;
                        await RefreshDisksAsync();
                        break;
                    }

                case "Shrink":
                    {
                        if (_selectedPartition == null) return;
                        if (!long.TryParse(_inputSizeMb?.Text?.Trim(), out long shrinkMb) || shrinkMb <= 0)
                        {
                            MessageBox.Show("Введите корректный размер сжатия в МБ.", "Ошибка ввода", MessageBoxButton.OK, MessageBoxImage.Warning);
                            return;
                        }

                        long shrinkBytes = shrinkMb * 1024L * 1024L;
                        var (ok, resMsg) = await PartitionManagementService.ShrinkPartitionAsync(_selectedPartition, shrinkBytes);
                        MessageBox.Show(resMsg, "Сжатие тома", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
                        if (ok) PartitionActionBox.Visibility = Visibility.Collapsed;
                        await RefreshDisksAsync();
                        break;
                    }

                case "Extend":
                    {
                        if (_selectedPartition == null) return;
                        long extendMb = 0;
                        if (!string.IsNullOrWhiteSpace(_inputSizeMb?.Text))
                        {
                            long.TryParse(_inputSizeMb.Text.Trim(), out extendMb);
                        }

                        long extendBytes = extendMb * 1024L * 1024L;
                        var (ok, resMsg) = await PartitionManagementService.ExtendPartitionAsync(_selectedPartition, extendBytes);
                        MessageBox.Show(resMsg, "Расширение тома", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
                        if (ok) PartitionActionBox.Visibility = Visibility.Collapsed;
                        await RefreshDisksAsync();
                        break;
                    }

                case "Format":
                    {
                        if (_selectedPartition == null) return;
                        string fs = (_comboFs?.SelectedItem as string) ?? "NTFS";
                        string label = string.IsNullOrWhiteSpace(_inputLabel?.Text) ? "Локальный диск" : _inputLabel.Text.Trim();
                        bool quick = _chkQuick?.IsChecked ?? true;

                        int cluster = 4096;
                        if (_comboCluster?.SelectedItem is string clusterStr)
                        {
                            string numOnly = new string(clusterStr.TakeWhile(char.IsDigit).ToArray());
                            if (int.TryParse(numOnly, out int cVal)) cluster = cVal;
                        }

                        var assessment = Win11CopyDialog.Modules.SafetyEngine.Services.ImpactPreviewService.AssessFormatting(
                            _selectedPartition.DriveLetter,
                            label,
                            fs,
                            _selectedPartition.SizeBytes);

                        var dlg = new Win11CopyDialog.Modules.SafetyEngine.Views.ImpactPreviewDialog(assessment)
                        {
                            Owner = Window.GetWindow(this)
                        };
                        if (dlg.ShowDialog() != true) return;

                        var (ok, resMsg) = await PartitionManagementService.FormatPartitionAsync(_selectedPartition, fs, label, quick, cluster);
                        MessageBox.Show(resMsg, "Форматирование", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
                        if (ok) PartitionActionBox.Visibility = Visibility.Collapsed;
                        await RefreshDisksAsync();
                        break;
                    }

                case "ChangeLetter":
                    {
                        if (_selectedPartition == null) return;
                        char? newLetter = null;
                        if (_comboLetter?.SelectedItem is string letterStr && letterStr.Length >= 2 && letterStr[1] == ':')
                        {
                            newLetter = letterStr[0];
                        }

                        var (ok, resMsg) = await PartitionManagementService.ChangeDriveLetterAsync(_selectedPartition.DiskNumber, _selectedPartition.PartitionNumber, newLetter ?? '\0');
                        MessageBox.Show(resMsg, "Буква тома", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
                        if (ok) PartitionActionBox.Visibility = Visibility.Collapsed;
                        await RefreshDisksAsync();
                        break;
                    }

                case "ChangeLabel":
                    {
                        if (_selectedPartition == null) return;
                        string newLabel = string.IsNullOrWhiteSpace(_inputLabel?.Text) ? "Диск" : _inputLabel.Text.Trim();
                        var (ok, resMsg) = await PartitionManagementService.ChangeVolumeLabelAsync(_selectedPartition.DriveLetter, newLabel);
                        MessageBox.Show(resMsg, "Метка тома", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
                        if (ok) PartitionActionBox.Visibility = Visibility.Collapsed;
                        await RefreshDisksAsync();
                        break;
                    }
            }
        }
        catch (OperationCanceledException)
        {
            MessageBox.Show("Операция отменена.", "Отмена", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            // Раньше здесь был try/finally БЕЗ catch. Ошибки diskpart/WMI
            // (нет доступа, диск занят, Win32Exception) приводили к фатальному
            // крашу посреди необратимой операции — полуформатированный том.
            MessageBox.Show(
                $"Операция «{_currentAction}» прервана ошибкой:\n\n{ex.Message}\n\n" +
                "Состояние накопителя может быть незавершённым. Рекомендуется проверить диск.",
                "Ошибка выполнения",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            ActionBoxExecuteBtn.IsEnabled = true;
        }
    }

    // ================= ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ ДЛЯ ФОРМЫ =================

    private static List<char> GetAvailableDriveLetters()
    {
        var used = DriveInfo.GetDrives()
            .Select(d => char.ToUpper(d.Name[0]))
            .ToHashSet();

        var list = new List<char>();
        for (char c = 'D'; c <= 'Z'; c++)
        {
            if (!used.Contains(c))
            {
                list.Add(c);
            }
        }
        return list;
    }

    /// <summary>
    /// Возвращает ресурс темы, либо null если он не найден.
    /// Раньше цвета форм были ЗАХАРДКОЖЕНЫ (тёмно-синий фон + белый текст),
    /// из-за чего в светлых темах (Light, MicaLight, MinimalWhite) форма
    /// «Создать раздел / Форматировать» выглядела инвертированной,
    /// а подсказки серым по тёмному были практически нечитаемы.
    /// </summary>
    private Brush? ThemeBrush(string key) => TryFindResource(key) as Brush;

    /// <summary>Фон поля ввода с корректным контрастом для активной темы.</summary>
    private Brush InputBackground() => ThemeBrush("ControlBackgroundBrush")
                                         ?? new SolidColorBrush(Color.FromArgb(180, 15, 23, 42));

    /// <summary>Цвет текста поля ввода.</summary>
    private Brush InputForeground() => ThemeBrush("PrimaryTextBrush") ?? Brushes.White;

    /// <summary>Цвет подписей и подсказок в форме.</summary>
    private Brush MutedForeground() => ThemeBrush("TextMutedBrush")
                                        ?? ThemeBrush("SecondaryTextBrush")
                                        ?? Brushes.LightGray;

    private TextBox CreateStyledTextBox(string initialValue)
    {
        return new TextBox
        {
            Text = initialValue,
            Padding = new Thickness(8, 5, 8, 5),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            BorderThickness = new Thickness(1),
            MinWidth = 200
        };
    }

    private ComboBox CreateStyledComboBox(IEnumerable<string> items, string selectedItem)
    {
        return new ComboBox
        {
            ItemsSource = items.ToList(),
            SelectedItem = selectedItem,
            Padding = new Thickness(8, 5, 8, 5),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            BorderThickness = new Thickness(1),
            MinWidth = 200
        };
    }

    private FrameworkElement CreateFormRow(string label, FrameworkElement control, string? tip = null)
    {
        // Применяем цвета активной темы ко всем элементам строки формы.
        var bg = InputBackground();
        var fg = InputForeground();
        var muted = MutedForeground();

        switch (control)
        {
            case TextBox tb:
                tb.Background = bg;
                tb.Foreground = fg;
                tb.BorderBrush = ThemeBrush("AccentBrush") ?? new SolidColorBrush(Color.FromArgb(100, 59, 130, 246));
                break;
            case ComboBox cb:
                cb.Background = bg;
                cb.Foreground = fg;
                cb.BorderBrush = ThemeBrush("AccentBrush") ?? new SolidColorBrush(Color.FromArgb(100, 59, 130, 246));
                break;
        }

        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };

        var lbl = new TextBlock
        {
            Text = label,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = muted,
            Margin = new Thickness(0, 0, 0, 4)
        };
        sp.Children.Add(lbl);
        sp.Children.Add(control);

        if (!string.IsNullOrEmpty(tip))
        {
            var hint = new TextBlock
            {
                Text = tip,
                FontSize = 10,
                // Brushes.DarkGray на тёмном фоне был практически нечитаем.
                Foreground = muted,
                Margin = new Thickness(0, 3, 0, 0)
            };
            sp.Children.Add(hint);
        }

        return sp;
    }

    private void TelemetryTimer_Tick(object? sender, EventArgs e)
    {
        if (_disks.Count == 0 || !IsVisible) return;
        var win = Window.GetWindow(this);
        if (win != null && win.WindowState == WindowState.Minimized) return;

        StorageMonitorService.PollRealtimeTelemetry(_disks);

        // Живые иконки получают те же реальные значения, что и текстовые
        // поля. Раньше данные телеметрии попадали только в надписи, а
        // индикаторы на карточках оставались декоративными.
        foreach (var d in _disks)
        {
            if (_liveIcons.TryGetValue(d.DiskNumber, out var icon))
            {
                icon.Model.Update(
                    d.CurrentReadSpeedMBps,
                    d.CurrentWriteSpeedMBps,
                    d.ActiveTimePercent);
            }
        }

        // Раньше здесь ТОЛЬКО опрашивались значения: ни один TextBlock и ни один
        // ProgressBar не обновлялись, а InvalidateVisual() не вызывался.
        // В итоге карточка «живого» здоровья показывала данные, загруженные
        // один раз при выборе диска: температура, износ и наработка «замерзали»,
        // хотя опрос WMI/SMART каждые 2 секунды продолжал работать впустую.
        UpdateRealtimeTelemetryUi();
    }

    /// <summary>Обновляет элементы карточки здоровья актуальными значениями телеметрии.</summary>
    private void UpdateRealtimeTelemetryUi()
    {
        var disk = _selectedDisk;
        if (disk == null) return;

        try
        {
            // Обновляем только те показатели, которые реально измерены.
            // Форматирование через свойства модели даёт «Нет данных»
            // вместо выдуманных чисел.
            DiskTempValueText.Text = disk.TemperatureFormatted;
            TempStatusBadgeText.Text = !disk.HasTemperature ? "—"
                : disk.TemperatureC < 50 ? "Норма"
                : disk.TemperatureC < 65 ? "Внимание"
                : "Троттлинг";
            TempBadgeBorder.Background = new BrushConverter().ConvertFromString(disk.TemperatureColor) as Brush;
            TempDescText.Text = disk.TemperatureStatus;

            DiskWearValueText.Text = disk.WearFormatted;
            DiskLifeDescText.Text = disk.TotalWrittenFormatted;

            DiskPowerHoursText.Text = disk.PowerOnHoursFormatted;
            DiskPowerCyclesText.Text = disk.PowerCyclesFormatted;

            DiskCapacitySummaryText.Text = $"Емкость: {Formatters.Bytes(disk.TotalSizeBytes - (long)disk.TotalFreeBytes)} занято из {disk.TotalSizeFormatted} ({disk.FreeSpacePercent:F1}% свободно)";
            DiskFreeSpaceBadgeText.Text = $"Свободно: {disk.FreeSpaceFormatted}";
            DiskSpaceProgressBar.SetSafe(disk.UsedSpacePercent);

            HealthScoreValueText.Text = disk.Score.IsCalculated
                ? $"{disk.Score.TotalScore:F0} / 100"
                : "— / 100";
            HealthScoreGradeText.Text = disk.Score.Grade;
            HealthScoreStatusText.Text = disk.Score.StatusText;
            HealthScoreStatusText.Foreground = new BrushConverter().ConvertFromString(disk.Score.StatusColor) as Brush;
        }
        catch
        {
            // Отказоустойчиво: телеметрия — фоновая функция, её сбой
            // не должен приводить к крашему интерфейса.
        }
    }

}
