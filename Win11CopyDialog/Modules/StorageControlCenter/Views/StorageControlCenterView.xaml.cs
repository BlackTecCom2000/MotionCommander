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
using Win11CopyDialog.Modules.StorageControlCenter.Models;
using Win11CopyDialog.Modules.StorageControlCenter.Services;

namespace Win11CopyDialog.Modules.StorageControlCenter.Views;

public partial class StorageControlCenterView : UserControl
{
    private List<StorageDisk> _disks = new();
    private StorageDisk? _selectedDisk;
    private StoragePartition? _selectedPartition;
    private readonly DispatcherTimer _telemetryTimer;
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
            _ => SubTabHealthRadio
        };
        target.IsChecked = true;
        SubTab_Checked(target, new RoutedEventArgs());
    }

    private async void StorageControlCenterView_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshDisksAsync();
        _telemetryTimer.Start();

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

    private void StorageControlCenterView_Unloaded(object sender, RoutedEventArgs e)
    {
        _telemetryTimer.Stop();
        _benchCts?.Cancel();
        _wipeCts?.Cancel();
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
    }

    private UIElement CreateDiskCard(StorageDisk disk)
    {
        var border = new Border
        {
            Width = 205,
            Height = 88,
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

        // Индикатор здоровья
        var healthPill = new Border
        {
            Background = (Brush)FindResource("ChipBackgroundBrush"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4, 1, 4, 1),
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var healthStack = new StackPanel { Orientation = Orientation.Horizontal };
        healthStack.Children.Add(new Ellipse
        {
            Width = 5,
            Height = 5,
            Fill = disk.Score.TotalScore >= 80 ? new SolidColorBrush(Color.FromRgb(16, 185, 129)) : (disk.Score.TotalScore >= 60 ? new SolidColorBrush(Color.FromRgb(245, 158, 11)) : new SolidColorBrush(Color.FromRgb(239, 68, 68))),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 3, 0)
        });
        healthStack.Children.Add(new TextBlock
        {
            Text = $"{disk.Score.Grade}",
            FontSize = 8.5,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("PrimaryTextBrush")
        });
        healthPill.Child = healthStack;
        statusStack.Children.Add(healthPill);

        // Температура
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
            Text = $"{disk.TemperatureC:F0}°C",
            FontSize = 8.5,
            FontWeight = FontWeights.Bold,
            Foreground = tempBrush
        };
        statusStack.Children.Add(tempPill);

        row0.Children.Add(statusStack);
        Grid.SetRow(row0, 0);
        mainGrid.Children.Add(row0);

        // --- ROW 1: Название модели диска ---
        var row1 = new StackPanel { Margin = new Thickness(0, 0, 0, 3) };
        var modelText = new TextBlock
        {
            Text = disk.Model,
            FontSize = 10.5,
            FontWeight = FontWeights.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)FindResource("PrimaryTextBrush")
        };
        row1.Children.Add(modelText);
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
            Content = $"Накопитель: {disk.Model}\nДиск: #{disk.DiskNumber} • Интерфейс: {disk.BusType}\nТип: {disk.MediaType} • Разметка: {disk.PartitionStyle}\nЕмкость: {disk.TotalSizeFormatted} (Свободно: {disk.FreeSpaceFormatted})\nЗдоровье: {disk.Score.TotalScore:F0}/100 ({disk.Score.Grade})\nТемпература: {disk.TemperatureC:F0}°C\nS/N: {disk.SerialNumber}"
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

        // Обновление ViewHealth
        HealthScoreGradeText.Text = disk.Score.Grade;
        HealthScoreValueText.Text = $"{disk.Score.TotalScore:F0} / 100";
        HealthScoreStatusText.Text = disk.Score.StatusText;
        HealthScoreStatusText.Foreground = new BrushConverter().ConvertFromString(disk.Score.StatusColor) as Brush;

        DiskTempValueText.Text = $"{disk.TemperatureC:F0} °C";
        TempStatusBadgeText.Text = disk.TemperatureC < 50 ? "Норма" : (disk.TemperatureC < 65 ? "Внимание" : "Троттлинг");
        TempBadgeBorder.Background = new BrushConverter().ConvertFromString(disk.TemperatureColor) as Brush;
        TempDescText.Text = disk.TemperatureStatus;

        DiskWearValueText.Text = $"{disk.LifetimeRemainingPercent:F0}% (Износ {disk.WearLevelPercent:F0}%)";
        DiskLifeDescText.Text = disk.TotalBytesWritten > 0 ? $"Записано: {Formatters.Bytes(disk.TotalBytesWritten)}" : "Ресурс ячеек в норме";

        DiskPowerHoursText.Text = $"{disk.PowerOnHours:N0} часов";
        DiskPowerCyclesText.Text = $"{disk.PowerCycles:N0} включений";

        DiskCapacitySummaryText.Text = $"Емкость: {Formatters.Bytes(disk.TotalSizeBytes - (long)disk.TotalFreeBytes)} занято из {disk.TotalSizeFormatted} ({disk.FreeSpacePercent:F1}% свободно)";
        DiskFreeSpaceBadgeText.Text = $"Свободно: {disk.FreeSpaceFormatted}";
        DiskSpaceProgressBar.Value = disk.UsedSpacePercent;

        // SMART атрибуты
        SmartAttributesList.ItemsSource = disk.SmartAttributes;

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
        if (ViewHealth == null || ViewPartitions == null || ViewBenchmark == null || ViewOptimizer == null || ViewCleanup == null || ViewSafety == null)
            return;

        if (sender == SubTabHealthRadio)
        {
            SubTabHealthRadio.IsChecked = true;
            SubTabPartitionsRadio.IsChecked = false;
            SubTabBenchmarkRadio.IsChecked = false;
            SubTabOptimizerRadio.IsChecked = false;
            SubTabCleanupRadio.IsChecked = false;
            SubTabSafetyRadio.IsChecked = false;
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
        }

        ViewHealth.Visibility = SubTabHealthRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ViewPartitions.Visibility = SubTabPartitionsRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ViewBenchmark.Visibility = SubTabBenchmarkRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ViewOptimizer.Visibility = SubTabOptimizerRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ViewCleanup.Visibility = SubTabCleanupRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ViewSafety.Visibility = SubTabSafetyRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void RefreshDrives_Click(object sender, RoutedEventArgs e)
    {
        await RefreshDisksAsync();
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
        if (_benchCts != null)
        {
            _benchCts.Cancel();
            _benchCts = null;
            StartBenchBtn.Content = "⚡ ЗАПУСТИТЬ ТЕСТ СКОРОСТИ";
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

        var config = new BenchmarkConfig
        {
            TargetDrive = target,
            FileSizeBytes = sizeBytes
        };

        _benchCts = new CancellationTokenSource();
        StartBenchBtn.Content = "⏹ ОСТАНОВИТЬ ТЕСТ";
        BenchProgressCard.Visibility = Visibility.Visible;
        BenchProgressBar.Value = 0;

        var progress = new Progress<(string testName, int percent, double currentSpeed)>(p =>
        {
            BenchProgressStatusText.Text = p.testName;
            BenchProgressBar.Value = p.percent;
            BenchLiveSpeedText.Text = $"{p.currentSpeed:F1} МБ/с";
        });

        try
        {
            var res = await StorageBenchmarkService.RunBenchmarkAsync(config, progress, _benchCts.Token);
            BenchmarkResultsList.ItemsSource = res.Items;
            BenchScoreSummaryText.Text = $"Общий рейтинг: {res.OverallPerformanceScore:F0} баллов";
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
            _benchCts = null;
            StartBenchBtn.Content = "⚡ ЗАПУСТИТЬ ТЕСТ СКОРОСТИ";
            BenchProgressCard.Visibility = Visibility.Collapsed;
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
        CleanupTotalReclaimableText.Text = "Сканирование временных файлов...";
        var items = await StorageCleanupService.ScanCleanupCategoriesAsync();
        CleanupCategoriesList.ItemsSource = items;

        long total = items.Sum(i => i.SizeBytes);
        CleanupTotalReclaimableText.Text = $"Найдено для очистки: {Formatters.Bytes(total)}";
    }

    private async void CleanNow_Click(object sender, RoutedEventArgs e)
    {
        if (CleanupCategoriesList.ItemsSource is not IEnumerable<StorageCleanupItem> items) return;

        CleanNowBtn.IsEnabled = false;
        var (cleanedBytes, deletedFiles) = await StorageCleanupService.CleanSelectedAsync(items);
        CleanNowBtn.IsEnabled = true;

        MessageBox.Show($"Очистка успешно завершена!\n\nОсвобождено места: {Formatters.Bytes(cleanedBytes)}\nУдалено временных файлов: {deletedFiles}", "Очистка кэша", MessageBoxButton.OK, MessageBoxImage.Information);
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
        finally
        {
            ScanLargeFilesBtn.IsEnabled = true;
        }
    }

    // ================= WIPE =================
    private async void StartWipe_Click(object sender, RoutedEventArgs e)
    {
        if (_wipeCts != null)
        {
            _wipeCts.Cancel();
            _wipeCts = null;
            StartWipeBtn.Content = "🛡 ЗАПУСТИТЬ ОЧИСТКУ СВОБОДНОГО МЕСТА";
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

        _wipeCts = new CancellationTokenSource();
        StartWipeBtn.Content = "⏹ ОСТАНОВИТЬ WIPE";
        WipeProgressBorder.Visibility = Visibility.Visible;
        WipeProgressBar.Value = 0;

        var progress = new Progress<(int percent, string status, double speedMBps)>(p =>
        {
            WipeProgressBar.Value = p.percent;
            WipeStatusText.Text = p.status;
            WipeSpeedText.Text = $"{p.speedMBps:F1} МБ/с";
        });

        try
        {
            var (success, msg) = await DiskWipeService.WipeFreeSpaceAsync(targetLetter, progress, _wipeCts.Token);
            MessageBox.Show(msg, "Очистка свободного места", MessageBoxButton.OK, success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (OperationCanceledException)
        {
            WipeStatusText.Text = "Операция отменена";
        }
        finally
        {
            _wipeCts = null;
            StartWipeBtn.Content = "🛡 ЗАПУСТИТЬ ОЧИСТКУ СВОБОДНОГО МЕСТА";
            WipeProgressBorder.Visibility = Visibility.Collapsed;
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
            var (success, msg) = await PartitionManagementService.DeletePartitionAsync(_selectedPartition, forceOverride: true);
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
            MessageBox.Show("Сжатие системного тома C: ограничено политиками безопасности Windows во время активной сессии.", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            var (success, msg) = await PartitionManagementService.CleanDiskAsync(_selectedDisk);
            MessageBox.Show(msg, "Очистка диска", MessageBoxButton.OK, success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            PartitionActionBox.Visibility = Visibility.Collapsed;
            await RefreshDisksAsync();
        }
    }

    private async void DiskClearReadOnly_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDisk == null) return;

        int? partNum = _selectedPartition?.PartitionNumber;
        var (success, msg) = await PartitionManagementService.ClearReadOnlyAsync(_selectedDisk.DiskNumber, partNum);
        MessageBox.Show(msg, "Снятие защиты от записи", MessageBoxButton.OK, success ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private async void PartChkdsk_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPartition == null || string.IsNullOrEmpty(_selectedPartition.DriveLetter))
        {
            MessageBox.Show("Для запуска Chkdsk выберите том, имеющий назначенную букву диска.", "Проверка Chkdsk", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var (success, msg) = await PartitionManagementService.CheckFileSystemAsync(_selectedPartition.DriveLetter);
        MessageBox.Show(msg, $"Chkdsk: {_selectedPartition.DriveLetter}:", MessageBoxButton.OK, success ? MessageBoxImage.Information : MessageBoxImage.Warning);
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

    private static TextBox CreateStyledTextBox(string initialValue)
    {
        return new TextBox
        {
            Text = initialValue,
            Padding = new Thickness(8, 5, 8, 5),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Background = new SolidColorBrush(Color.FromArgb(180, 15, 23, 42)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromArgb(100, 59, 130, 246)),
            BorderThickness = new Thickness(1),
            MinWidth = 200
        };
    }

    private static ComboBox CreateStyledComboBox(IEnumerable<string> items, string selectedItem)
    {
        var cb = new ComboBox
        {
            ItemsSource = items.ToList(),
            SelectedItem = selectedItem,
            Padding = new Thickness(8, 5, 8, 5),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Background = new SolidColorBrush(Color.FromArgb(180, 15, 23, 42)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromArgb(100, 59, 130, 246)),
            BorderThickness = new Thickness(1),
            MinWidth = 200
        };
        return cb;
    }

    private static FrameworkElement CreateFormRow(string label, FrameworkElement control, string? tip = null)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };

        var lbl = new TextBlock
        {
            Text = label,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.LightGray,
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
                Foreground = Brushes.DarkGray,
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
    }
}
