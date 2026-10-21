using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Win11CopyDialog.Modules.GameTuning;
using Win11CopyDialog.Modules.TajArena.Models;
using Win11CopyDialog.Modules.TajArena.Services;

namespace Win11CopyDialog.Views;

/// <summary>
/// Экран оптимизации игр: реальные настройки Windows с проверкой записи,
/// правка конфигов игр только по существующим ключам и измерение FPS
/// через ETW с честным вердиктом о погрешности.
/// </summary>
public partial class GameOptimizationView : UserControl
{
    private readonly GameTuningService _service = GameTuningService.Instance;

    private HardwareInfo _hardware = new();
    private string? _selectedExe;
    private bool _initialized;
    private bool _busy;

    public GameOptimizationView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Вызывается при первом показе вкладки. Автопоиск игр и опрос
    /// оборудования не выполняются при старте программы: они не должны
    /// задерживать открытие файлового менеджера.
    /// </summary>
    public async Task ActivateAsync()
    {
        if (_initialized) return;
        _initialized = true;
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        try
        {
            SetOperationStatus("Чтение параметров оборудования и состояния настроек…", false);

            _hardware = await Task.Run(HardwareProbe.Probe);
            RenderHardware();

            AdminBadgeText.Text = GameTuningService.IsElevated
                ? "Права администратора: есть — доступны настройки HKLM, HAGS и измерение FPS через ETW."
                : "Права администратора: нет — системные настройки HKLM, HAGS и замер FPS недоступны. Остальное работает.";
            AdminBadgeText.Foreground = (Brush)(TryFindResource(GameTuningService.IsElevated ? "AccentBrush" : "SecondaryTextBrush") ?? Brushes.Gray);

            BuildTweakRows();
            BuildPerGameRows();
            RefreshPresetPreview();
            await LoadGamesAsync();
            RefreshFpsHistory();

            SetOperationStatus("Готово. Состояние прочитано из системы, значения не изменялись.", false);
        }
        catch (Exception ex)
        {
            SetOperationStatus("Ошибка чтения состояния: " + ex.Message, true);
        }
    }

    // ───────────────────────── железо ─────────────────────────

    private void RenderHardware()
    {
        HardwarePanel.Children.Clear();
        AddChip("Процессор", _hardware.CpuLine);
        AddChip("Видеокарта", _hardware.GpuLine);
        AddChip("ОЗУ", _hardware.RamLine);
        AddChip("Экран", _hardware.DisplayLine);
        AddChip("ОС", _hardware.OsLine);
        AddChip("Форм-фактор", _hardware.FormFactorLine);
        AddChip("Замер FPS", FpsMonitor.CanMeasure ? "доступен (ETW)" : "нужны права администратора");
    }

    private void AddChip(string title, string value)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 6, 6)
        };
        border.SetResourceReference(Border.BackgroundProperty, "CardBackgroundBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "GlassBorderBrush");

        var stack = new StackPanel();
        var caption = new TextBlock
        {
            Text = title.ToUpperInvariant(),
            FontSize = 8.5,
            FontWeight = FontWeights.Bold,
            Opacity = 0.7
        };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");

        var text = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(value) ? "н/д" : value,
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            MaxWidth = 420,
            TextWrapping = TextWrapping.Wrap
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");

        stack.Children.Add(caption);
        stack.Children.Add(text);
        border.Child = stack;
        HardwarePanel.Children.Add(border);
    }

    // ───────────────────────── игры ─────────────────────────

    private sealed class GameChoice
    {
        public string Display { get; }
        public string? Exe { get; }

        public GameChoice(string display, string? exe)
        {
            Display = display;
            Exe = exe;
        }

        public override string ToString() => Display;
    }

    private async Task LoadGamesAsync()
    {
        GameCombo.IsEnabled = false;
        RefreshGamesBtn.IsEnabled = false;
        GamePathText.Text = "Поиск установленных игр…";

        try
        {
            DiscoveryResult result = await Task.Run(GameDiscoveryService.Discover);

            var choices = new List<GameChoice>();
            foreach (GameEntry game in result.Games
                         .Where(g => !g.Hidden)
                         .OrderBy(g => g.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            {
                string? exe = ResolveExe(game);
                if (exe == null) continue;
                choices.Add(new GameChoice($"{game.DisplayName}  •  {game.PlatformName}", exe));
            }

            GameCombo.Items.Clear();
            foreach (GameChoice choice in choices) GameCombo.Items.Add(choice);

            if (choices.Count > 0)
            {
                GameCombo.SelectedIndex = 0;
                GamePathText.Text = $"Найдено игр: {choices.Count}. Выберите игру, затем примените настройки.";
            }
            else
            {
                GamePathText.Text = "Автопоиск не нашёл игр с известным exe. Нажмите «Указать exe…».";
            }
        }
        catch (Exception ex)
        {
            GamePathText.Text = "Ошибка поиска игр: " + ex.Message;
        }
        finally
        {
            GameCombo.IsEnabled = true;
            RefreshGamesBtn.IsEnabled = true;
        }
    }

    private static string? ResolveExe(GameEntry game)
    {
        if (!string.IsNullOrWhiteSpace(game.MainExePath) && File.Exists(game.MainExePath))
            return game.MainExePath;

        foreach (string candidate in game.CandidateExePaths)
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                return candidate;

        return null;
    }

    private void GameCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GameCombo.SelectedItem is not GameChoice choice) return;

        _selectedExe = choice.Exe;
        GamePathText.Text = string.IsNullOrWhiteSpace(_selectedExe)
            ? "Игра не выбрана"
            : "exe: " + _selectedExe;

        BuildPerGameRows();
        RefreshPresetPreview();
        RefreshFpsHistory();
    }

    private async void RefreshGamesBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        try { await LoadGamesAsync(); }
        finally { _busy = false; }
    }

    private void PickExeBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите exe игры",
            Filter = "Исполняемые файлы (*.exe)|*.exe|Все файлы (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() != true) return;

        string display = Path.GetFileNameWithoutExtension(dialog.FileName) + "  •  указано вручную";
        var choice = new GameChoice(display, dialog.FileName);

        GameCombo.Items.Add(choice);
        GameCombo.SelectedItem = choice;

        SetOperationStatus("Выбрана игра: " + dialog.FileName, false);
    }

    // ───────────────────────── твики ─────────────────────────

    private void BuildTweakRows()
    {
        if (SystemTweaksHost == null) return;
        SystemTweaksHost.Children.Clear();

        foreach (IGrouping<TweakGroup, GameTweak> group in _service.Tweaks
                     .Where(t => t.Scope == TweakScope.System)
                     .GroupBy(t => t.Group))
        {
            SystemTweaksHost.Children.Add(BuildGroupHeader(group.Key));
            foreach (GameTweak tweak in group)
                SystemTweaksHost.Children.Add(BuildTweakCard(tweak));
        }
    }

    private void BuildPerGameRows()
    {
        if (PerGameHost == null) return;
        PerGameHost.Children.Clear();

        if (string.IsNullOrWhiteSpace(_selectedExe))
        {
            var hint = new TextBlock
            {
                Text = "Сначала выберите игру — настройки применяются к конкретному exe.",
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 4)
            };
            hint.SetResourceReference(TextBlock.StyleProperty, "MutedText");
            PerGameHost.Children.Add(hint);
            return;
        }

        foreach (GameTweak tweak in _service.Tweaks.Where(t => t.Scope == TweakScope.PerGame))
            PerGameHost.Children.Add(BuildTweakCard(tweak));
    }

    private TextBlock BuildGroupHeader(TweakGroup group)
    {
        var header = new TextBlock
        {
            Text = _service.Tweaks.First(t => t.Group == group).GroupTitle.ToUpperInvariant(),
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            Opacity = 0.7,
            Margin = new Thickness(0, 10, 0, 5)
        };
        header.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
        return header;
    }

    private Border BuildTweakCard(GameTweak tweak)
    {
        TweakStatusInfo status = _service.GetStatus(tweak, _selectedExe);
        bool needsExe = tweak.Scope == TweakScope.PerGame && string.IsNullOrWhiteSpace(_selectedExe);

        var card = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 6)
        };
        card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "GlassBorderBrush");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };

        var titleRow = new WrapPanel();
        var title = new TextBlock
        {
            Text = tweak.Title,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 6, 0)
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");
        titleRow.Children.Add(title);

        titleRow.Children.Add(new TextBlock
        {
            Text = "[" + tweak.EffectTitle + "]",
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            Foreground = EffectBrush(tweak.Effect),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 1, 6, 0)
        });

        if (tweak.RequiresAdmin)
            titleRow.Children.Add(Badge("нужен админ", "#F59E0B"));
        if (tweak.RequiresReboot)
            titleRow.Children.Add(Badge("перезагрузка", "#60A5FA"));
        if (tweak.Risk != TweakRisk.Safe)
            titleRow.Children.Add(Badge(tweak.RiskTitle, "#A78BFA"));

        left.Children.Add(titleRow);

        var statusLine = new TextBlock
        {
            FontSize = 10,
            Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Text = "Состояние: " + status.StatusText +
                   (string.IsNullOrWhiteSpace(status.Current) ? "" : "  •  " + status.Current)
        };
        statusLine.Foreground = status.Status == TweakStatus.Applied
            ? new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81))
            : (Brush)(TryFindResource("SecondaryTextBrush") ?? Brushes.Gray);
        left.Children.Add(statusLine);

        var mechanism = new TextBlock
        {
            Text = tweak.Mechanism,
            FontSize = 9.8,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0)
        };
        mechanism.SetResourceReference(TextBlock.StyleProperty, "MutedText");
        left.Children.Add(mechanism);

        if (!string.IsNullOrWhiteSpace(tweak.Limitation))
        {
            var limitation = new TextBlock
            {
                Text = "Ограничение: " + tweak.Limitation,
                FontSize = 9.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
                Opacity = 0.85
            };
            limitation.SetResourceReference(TextBlock.StyleProperty, "MutedText");
            left.Children.Add(limitation);
        }

        if (!string.IsNullOrWhiteSpace(status.Detail) && status.Status == TweakStatus.Unavailable)
        {
            left.Children.Add(new TextBlock
            {
                Text = "Причина: " + status.Detail,
                FontSize = 9.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            });
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center
        };

        var apply = new Button
        {
            Content = tweak.IsAction ? "Выполнить" : "Применить",
            Padding = new Thickness(10, 4, 10, 4),
            MinWidth = 96,
            Margin = new Thickness(0, 0, 0, 4),
            Style = (Style)(TryFindResource("CyberButton") ?? new Style())
        };
        apply.IsEnabled = !needsExe
                          && (!tweak.RequiresAdmin || GameTuningService.IsElevated)
                          && (tweak.IsAction || status.Status != TweakStatus.Applied);
        if (tweak.RequiresAdmin && !GameTuningService.IsElevated)
            apply.ToolTip = "Нужны права администратора";
        else if (needsExe)
            apply.ToolTip = "Сначала выберите игру";
        apply.Click += async (_, _) => await RunTweakAsync(tweak, apply: true);
        buttons.Children.Add(apply);

        if (!tweak.IsAction)
        {
            var revert = new Button
            {
                Content = "Откатить",
                Padding = new Thickness(10, 4, 10, 4),
                MinWidth = 96,
                Style = (Style)(TryFindResource("CyberToolButton") ?? new Style())
            };
            revert.IsEnabled = !needsExe
                               && (status.Status == TweakStatus.Applied || _service.HasBackup(tweak.Id, _selectedExe));
            revert.Click += async (_, _) => await RunTweakAsync(tweak, apply: false);
            buttons.Children.Add(revert);
        }

        Grid.SetColumn(left, 0);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(left);
        grid.Children.Add(buttons);
        card.Child = grid;
        return card;
    }

    private static TextBlock Badge(string text, string color)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 8.5,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)new BrushConverter().ConvertFromString(color)!,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 1, 6, 0),
            Opacity = 0.9
        };
    }

    private static Brush EffectBrush(TweakEffectKind effect) => effect switch
    {
        TweakEffectKind.Fps => new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)),
        TweakEffectKind.FrameTime => new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA)),
        TweakEffectKind.InputLatency => new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),
        TweakEffectKind.Memory => new SolidColorBrush(Color.FromRgb(0xA7, 0x8B, 0xFA)),
        _ => new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8))
    };

    private async Task RunTweakAsync(GameTweak tweak, bool apply)
    {
        if (_busy) return;
        _busy = true;
        SetBusy(true);

        try
        {
            TweakOperationResult result = await Task.Run(() =>
                apply ? _service.Apply(tweak, _selectedExe) : _service.Revert(tweak, _selectedExe));

            SetOperationStatus((apply ? "Применение: " : "Откат: ") + tweak.Title + " — " + result.Message, !result.Ok);

            BuildTweakRows();
            BuildPerGameRows();
            RefreshPresetPreview();
        }
        catch (Exception ex)
        {
            SetOperationStatus("Ошибка: " + ex.Message, true);
        }
        finally
        {
            _busy = false;
            SetBusy(false);
        }
    }

    private async void ApplyRecommendedBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var recommendations = _service.BuildRecommendations(_hardware, _selectedExe);
        if (recommendations.Count == 0)
        {
            SetOperationStatus("Рекомендаций нет: рекомендуемые настройки уже применены.", false);
            return;
        }

        string list = string.Join("\n", recommendations.Select(r => "• " + r.Title + " — " + r.Basis));
        MessageBoxResult confirm = MessageBox.Show(
            "Будет применено настроек: " + recommendations.Count + "\n\n" + list +
            "\n\nПеред каждым изменением сохраняется исходное значение. Откат — кнопкой «Откатить всё».",
            "Оптимизация игр",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        _busy = true;
        SetBusy(true);
        try
        {
            TweakOperationResult result = await Task.Run(() => _service.ApplyRecommended(_hardware, _selectedExe));
            SetOperationStatus(result.Message, !result.Ok);

            BuildTweakRows();
            BuildPerGameRows();
            RefreshPresetPreview();
        }
        finally
        {
            _busy = false;
            SetBusy(false);
        }
    }

    private async void RevertAllBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        int count = _service.BackupCount;
        if (count == 0)
        {
            SetOperationStatus("Откатывать нечего: исходные значения ещё не сохранялись.", false);
            return;
        }

        MessageBoxResult confirm = MessageBox.Show(
            $"Вернуть исходные значения для {count} изменений?\n\n" +
            "Будут восстановлены именно те значения, которые были до изменения (включая удаление созданных).",
            "Откат настроек",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        _busy = true;
        SetBusy(true);
        try
        {
            List<(string TweakId, string Message)> results = await Task.Run(() => _service.RevertAll());
            string text = string.Join("\n", results.Select(r => "• " + r.TweakId + ": " + r.Message));
            SetOperationStatus("Откат:\n" + text, results.Any(r => r.Message.Contains("не удалось") || r.Message.Contains("не полностью")));

            BuildTweakRows();
            BuildPerGameRows();
            RefreshPresetPreview();
        }
        finally
        {
            _busy = false;
            SetBusy(false);
        }
    }

    private async void RefreshAllBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        SetBusy(true);
        try
        {
            _hardware = await Task.Run(HardwareProbe.Probe);
            RenderHardware();
            BuildTweakRows();
            BuildPerGameRows();
            RefreshPresetPreview();
            RefreshFpsHistory();
            SetOperationStatus("Состояние перечитано из системы.", false);
        }
        finally
        {
            _busy = false;
            SetBusy(false);
        }
    }

    // ───────────────────────── конфиги игр ─────────────────────────

    private sealed class PresetChoice
    {
        public string Title { get; }
        public string Id { get; }

        public PresetChoice(string title, string id)
        {
            Title = title;
            Id = id;
        }

        public override string ToString() => Title;
    }

    private string? SelectedPresetId => (PresetCombo?.SelectedItem as PresetChoice)?.Id;

    private void EnsurePresetItems()
    {
        if (PresetCombo.Items.Count > 0) return;

        foreach (ConfigPresetInfo preset in GameConfigPresets.Presets)
            PresetCombo.Items.Add(new PresetChoice(preset.Title, preset.Id));

        if (PresetCombo.Items.Count > 0) PresetCombo.SelectedIndex = 0;
    }

    private void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshPresetPreview();
    }

    private void RefreshPresetPreview()
    {
        if (PresetCombo == null || PresetInfoText == null) return;

        EnsurePresetItems();
        string? presetId = SelectedPresetId;
        if (string.IsNullOrEmpty(presetId)) return;

        ConfigPreview preview = GameConfigPresets.BuildPreview(presetId, _selectedExe, _hardware.RefreshHz);

        string text = (string.IsNullOrEmpty(preview.FilePath) ? "Файл: не найден" : "Файл: " + preview.FilePath) +
                      "\n" + preview.Summary;
        if (preview.GameRunning)
            text += "\n⚠ Игра запущена: закройте её, иначе конфиг будет перезаписан при выходе.";
        text += "\n" + preview.Note;

        PresetInfoText.Text = text;

        PresetChangesHost.Children.Clear();
        foreach (ConfigChange change in preview.Changes)
        {
            var line = new TextBlock
            {
                Text = (change.Found ? "✓ " : "— ") + change.Line,
                FontSize = 9.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 1, 0, 0),
                Foreground = change.Found
                    ? new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81))
                    : (Brush)(TryFindResource("SecondaryTextBrush") ?? Brushes.Gray)
            };
            PresetChangesHost.Children.Add(line);
        }

        ApplyPresetBtn.IsEnabled = preview.FileExists && preview.FoundCount > 0 && !preview.GameRunning;
        RestorePresetBtn.IsEnabled = !string.IsNullOrEmpty(GameConfigPresets.FindLatestBackup(presetId, _selectedExe));
    }

    private async void ApplyPresetBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        string? presetId = SelectedPresetId;
        if (string.IsNullOrEmpty(presetId)) return;

        ConfigPreview preview = GameConfigPresets.BuildPreview(presetId, _selectedExe, _hardware.RefreshHz);
        if (!preview.FileExists)
        {
            SetOperationStatus("Файл настроек ещё не создан: запустите игру один раз, выйдите и повторите.", true);
            return;
        }

        if (preview.GameRunning)
        {
            SetOperationStatus("Игра запущена: закройте её, иначе она перезапишет конфиг при выходе.", true);
            return;
        }

        var confirm = MessageBox.Show(
            $"Изменить {preview.FoundCount} существующих ключей в файле:\n{preview.FilePath}\n\n" +
            "Будет создана резервная копия. Отсутствующие ключи не добавляются.",
            "Пресет конфига",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        _busy = true;
        SetBusy(true);
        try
        {
            TweakOperationResult result = await Task.Run(() =>
                GameConfigPresets.Apply(presetId, _selectedExe, _hardware.RefreshHz));

            SetOperationStatus(result.Message, !result.Ok);
            RefreshPresetPreview();
        }
        finally
        {
            _busy = false;
            SetBusy(false);
        }
    }

    private async void RestorePresetBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        string? presetId = SelectedPresetId;
        if (string.IsNullOrEmpty(presetId)) return;

        _busy = true;
        SetBusy(true);
        try
        {
            TweakOperationResult result = await Task.Run(() => GameConfigPresets.Restore(presetId, _selectedExe));
            SetOperationStatus(result.Message, !result.Ok);
            RefreshPresetPreview();
        }
        finally
        {
            _busy = false;
            SetBusy(false);
        }
    }

    // ───────────────────────── FPS ─────────────────────────

    private void EnsureFpsSecondsItems()
    {
        if (FpsSecondsCombo.Items.Count > 0) return;

        foreach (int seconds in new[] { 10, 20, 30, 60 })
        {
            FpsSecondsCombo.Items.Add(new ComboBoxItem
            {
                Content = seconds + " секунд",
                Tag = seconds
            });
        }
        FpsSecondsCombo.SelectedIndex = 0;
    }

    private int SelectedFpsSeconds
    {
        get
        {
            EnsureFpsSecondsItems();
            return FpsSecondsCombo.SelectedItem is ComboBoxItem { Tag: int seconds } ? seconds : 10;
        }
    }

    private async void MeasureFpsBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (string.IsNullOrWhiteSpace(_selectedExe))
        {
            FpsStatusText.Text = "Сначала выберите игру.";
            return;
        }

        string processName = Path.GetFileNameWithoutExtension(_selectedExe!);
        int? pid = FpsMonitor.FindProcessId(processName);
        if (!pid.HasValue)
        {
            FpsStatusText.Text = $"Процесс «{processName}» не запущен. Запустите игру и повторите замер.";
            return;
        }

        if (!FpsMonitor.CanMeasure)
        {
            FpsStatusText.Text = "НЕ ИЗМЕРЕНО: " + FpsMonitor.CapabilityText +
                                 ". Запустите Motion Commander от имени администратора и повторите.";
            return;
        }

        await MeasureAsync(pid.Value, processName, SelectedFpsSeconds, save: true);
    }

    private async void DwmTestBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        int? pid = FpsMonitor.FindProcessId("dwm");
        if (!pid.HasValue)
        {
            FpsStatusText.Text = "Процесс dwm не найден — проверить ETW-конвейер на композиции рабочего стола нельзя.";
            return;
        }

        if (!FpsMonitor.CanMeasure)
        {
            FpsStatusText.Text = "НЕ ИЗМЕРЕНО: " + FpsMonitor.CapabilityText +
                                 ". Запустите Motion Commander от имени администратора.";
            return;
        }

        await MeasureAsync(pid.Value, "dwm", 5, save: false);
    }

    private async Task MeasureAsync(int pid, string processName, int seconds, bool save)
    {
        _busy = true;
        MeasureFpsBtn.IsEnabled = false;
        DwmTestBtn.IsEnabled = false;
        FpsProgressBar.Visibility = Visibility.Visible;
        FpsProgressBar.Value = 0;
        FpsResultText.Text = "";
        FpsComparisonText.Text = "";
        FpsStatusText.Text = $"Замер {processName}: {seconds} с…";

        try
        {
            FpsResult? previous = save ? _service.FpsSessions.Latest(processName) : null;

            var progress = new Progress<FpsProgress>(p =>
            {
                FpsProgressBar.Value = p.Fraction;
                FpsStatusText.Text = $"Замер {processName}: {p.ElapsedSeconds:0.0} / {seconds} с, кадров {p.Frames}";
            });

            FpsResult result = await FpsMonitor.MeasureAsync(pid, TimeSpan.FromSeconds(seconds), progress);

            FpsStatusText.Text = save
                ? "Замер завершён (источник: ETW Present)."
                : "Проверка измерителя на DWM завершена. Это частота композиции рабочего стола, а не FPS игры.";
            FpsResultText.Text = result.Line;

            if (save)
            {
                _service.FpsSessions.Append(result);
                _service.Audit("FPS", "measure:" + processName, null, result.Line);

                if (previous != null)
                {
                    FpsComparison comparison = FpsStats.Compare(previous, result);
                    FpsComparisonText.Text =
                        $"Предыдущий замер ({previous.MeasuredAtUtc.ToLocalTime():HH:mm:ss}): {previous.ShortSummary}.\n" +
                        comparison.Verdict +
                        "\nСравнение корректно только при одинаковой сцене и тех же настройках графики.";
                }
                else
                {
                    FpsComparisonText.Text =
                        "Это первый замер этого процесса — база для сравнения. " +
                        "Примените настройки, повторите замер и сравните результат.";
                }

                RefreshFpsHistory();
            }
        }
        catch (FpsUnavailableException ex)
        {
            FpsStatusText.Text = "НЕ ИЗМЕРЕНО: " + ex.Message;
        }
        catch (Exception ex)
        {
            FpsStatusText.Text = "Ошибка замера: " + ex.Message;
        }
        finally
        {
            FpsProgressBar.Visibility = Visibility.Collapsed;
            MeasureFpsBtn.IsEnabled = true;
            DwmTestBtn.IsEnabled = true;
            _busy = false;
        }
    }

    private void RefreshFpsHistory()
    {
        if (FpsHistoryText == null) return;

        string? process = string.IsNullOrWhiteSpace(_selectedExe)
            ? null
            : Path.GetFileNameWithoutExtension(_selectedExe);

        List<FpsResult> sessions = _service.FpsSessions.Load();
        if (process != null)
            sessions = sessions.Where(s => string.Equals(s.ProcessName, process, StringComparison.OrdinalIgnoreCase)).ToList();

        FpsHistoryText.Text = sessions.Count == 0
            ? "История замеров: пусто. Замеры сохраняются в профиле пользователя."
            : "История замеров: " + sessions.Count + ". Последние: " +
              string.Join("; ", sessions.TakeLast(3).Select(s =>
                  $"{s.MeasuredAtUtc.ToLocalTime():dd.MM HH:mm} — {s.ShortSummary}"));
    }

    // ───────────────────────── служебное ─────────────────────────

    private void SetBusy(bool busy)
    {
        ApplyRecommendedBtn.IsEnabled = !busy;
        RevertAllBtn.IsEnabled = !busy;
        RefreshAllBtn.IsEnabled = !busy;
    }

    private void SetOperationStatus(string text, bool error)
    {
        if (OperationStatusText == null) return;

        OperationStatusText.Text = text;
        OperationStatusText.Foreground = error
            ? new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44))
            : (Brush)(TryFindResource("AccentBrush") ?? Brushes.LightBlue);
    }
}
