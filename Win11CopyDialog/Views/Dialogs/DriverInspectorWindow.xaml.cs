using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Win11CopyDialog.Helpers;
using Win11CopyDialog.Models;

namespace Win11CopyDialog.Views.Dialogs;

public sealed class DriverDeviceInfo
{
    public string Name { get; set; } = "";
    public string DeviceID { get; set; } = "";
    public string HardwareId { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string PNPClass { get; set; } = "";
    public string CategoryName { get; set; } = "";
    public string CategoryIcon { get; set; } = "🧩";
    public string ServiceName { get; set; } = "";
    public string Status { get; set; } = "OK";
    public uint ConfigManagerErrorCode { get; set; }
    public bool HasError => ConfigManagerErrorCode != 0;

    public string StatusBadgeText => ConfigManagerErrorCode switch
    {
        0 => "OK",
        22 => "ОТКЛЮЧЕН",
        28 => "НЕТ ДРАЙВЕРА",
        _ => "СБОЙ"
    };

    // Раньше эти два свойства создавали НОВУЮ SolidColorBrush при каждом
    // обращении, а биндинг WPF вызывает их дважды на строку при каждой
    // перерисовке. Теперь кисти создаются один раз и переиспользуются
    // (Freeze обязателен — иначе кисть заблокирует UI-поток).
    private static readonly Brush _okBadge = Frozen(Color.FromArgb(40, 0, 230, 118));
    private static readonly Brush _warnBadge = Frozen(Color.FromArgb(40, 255, 179, 0));
    private static readonly Brush _errBadge = Frozen(Color.FromArgb(40, 255, 82, 82));

    private static readonly Brush _okFg = Frozen(Color.FromRgb(0, 230, 118));
    private static readonly Brush _warnFg = Frozen(Color.FromRgb(255, 179, 0));
    private static readonly Brush _errFg = Frozen(Color.FromRgb(255, 82, 82));

    private static SolidColorBrush Frozen(Color color)
    {
        var b = new SolidColorBrush(color);
        b.Freeze();
        return b;
    }

    public Brush StatusBadgeBrush => ConfigManagerErrorCode switch
    {
        0 => _okBadge,
        22 => _warnBadge,
        _ => _errBadge
    };

    public Brush StatusBadgeForeground => ConfigManagerErrorCode switch
    {
        0 => _okFg,
        22 => _warnFg,
        _ => _errFg
    };

    public string ErrorDescription => ConfigManagerErrorCode switch
    {
        0 => "Работает исправно (Код 0)",
        1 => "Устройство не настроено (Код 1)",
        10 => "Запуск устройства невозможен (Код 10)",
        14 => "Требуется перезагрузка компьютера (Код 14)",
        18 => "Переустановите драйверы (Код 18)",
        22 => "Устройство отключено пользователем (Код 22)",
        28 => "Для устройства не установлены драйверы (Код 28)",
        31 => "Устройство работает неправильно (Код 31)",
        43 => "Остановлено из-за ошибки в работе (Код 43)",
        _ => $"Код состояния Windows: {ConfigManagerErrorCode}"
    };

    public Brush ErrorTextBrush => ConfigManagerErrorCode == 0
        ? new SolidColorBrush(Color.FromRgb(150, 160, 175))
        : new SolidColorBrush(Color.FromRgb(255, 82, 82));
}

public partial class DriverInspectorWindow : Window
{
    private readonly List<DriverDeviceInfo> _allDevices = new();

    /// <summary>Сообщение об ошибке последнего частичного сбоя при WMI-сканировании.</summary>
    private string? _lastScanError;

    public DriverInspectorWindow()
    {
        InitializeComponent();
        ThemeManager.Instance.Apply();
        BackdropHelper.Apply(this, ThemeManager.Instance.Backdrop, ThemeManager.Instance.IsDark);

        Loaded += async (_, _) => await LoadDriversAsync();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        Close();
    }

    private async void RefreshDrivers_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        await LoadDriversAsync();
    }

    private async Task LoadDriversAsync()
    {
        RefreshBtn.IsEnabled = false;
        ScanProgress.Visibility = Visibility.Visible;
        StatusText.Text = "Сканирование оборудования и базы драйверов Windows...";

        var sw = Stopwatch.StartNew();
        _allDevices.Clear();

        string? scanError = null;

        try
        {
            var list = await Task.Run(() =>
            {
                var result = new List<DriverDeviceInfo>();
                string? error = null;

                using var searcher = new ManagementObjectSearcher(
                    "SELECT DeviceID, Name, Caption, Description, Manufacturer, PNPClass, Service, Status, ConfigManagerErrorCode, HardWareID FROM Win32_PnPEntity");

                // Раньше try стоял ВОКРУГ всего foreach, поэтому одно
                // некорректное устройство прерывало перечисление, а ошибка
                // уходила только в Debug.WriteLine (вырезается в Release).
                // Пользователю показывалось «Найдено устройств: 0» без причины.
                foreach (ManagementObject mo in searcher.Get())
                {
                    // Каждый ManagementObject удерживает COM-ссылку
                    // IWbemClassObject и обязан быть освобождён. Раньше
                    // освобождался только searcher, что при 200–1000 устройствах
                    // давало утечку COM-объектов на всё время работы процесса.
                    using (mo)
                    {
                        try
                        {
                            string name = mo["Name"]?.ToString() ?? mo["Caption"]?.ToString() ?? mo["Description"]?.ToString() ?? "Неизвестное устройство";
                            string pnpClass = mo["PNPClass"]?.ToString() ?? "";
                            string mfg = mo["Manufacturer"]?.ToString() ?? "Стандартный";
                            string devId = mo["DeviceID"]?.ToString() ?? "";
                            string service = mo["Service"]?.ToString() ?? "—";
                            string status = mo["Status"]?.ToString() ?? "OK";
                            uint errCode = 0;
                            if (mo["ConfigManagerErrorCode"] != null)
                            {
                                _ = uint.TryParse(mo["ConfigManagerErrorCode"].ToString(), out errCode);
                            }

                            string hwId = "";
                            if (mo["HardWareID"] is string[] hwArray && hwArray.Length > 0)
                            {
                                hwId = hwArray[0];
                            }
                            else
                            {
                                hwId = devId;
                            }

                            var (catName, catIcon) = Categorize(pnpClass, name);

                            result.Add(new DriverDeviceInfo
                            {
                                Name = name,
                                DeviceID = devId,
                                HardwareId = hwId,
                                Manufacturer = mfg,
                                PNPClass = pnpClass,
                                CategoryName = catName,
                                CategoryIcon = catIcon,
                                ServiceName = service,
                                Status = status,
                                ConfigManagerErrorCode = errCode
                            });
                        }
                        catch (Exception ex)
                        {
                            // Одно сбойное устройство не должно прерывать
                            // сканирование всей системы.
                            error ??= ex.Message;
                        }
                    }
                }

                _lastScanError = error;
                return result;
            });

            _allDevices.AddRange(list.OrderBy(d => d.HasError ? 0 : 1).ThenBy(d => d.CategoryName).ThenBy(d => d.Name));
            ApplyFilters();

            int errCount = _allDevices.Count(d => d.HasError);
            scanError = _lastScanError;

            StatusText.Text = $"Найдено устройств: {_allDevices.Count} (с ошибками/отключено: {errCount}) за {sw.Elapsed.TotalSeconds:F1} сек."
                            + (scanError != null ? $"\n⚠ Часть устройств пропущена: {scanError}" : "");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Ошибка сканирования: {ex.Message}";
        }
        finally
        {
            RefreshBtn.IsEnabled = true;
            ScanProgress.Visibility = Visibility.Collapsed;
        }
    }

    private static (string Name, string Icon) Categorize(string pnpClass, string devName)
    {
        string cls = pnpClass.ToUpperInvariant();
        string nameUpper = devName.ToUpperInvariant();

        if (cls.Contains("DISK") || cls.Contains("SCSI") || cls.Contains("HDC") || cls.Contains("STORAGE") || nameUpper.Contains("NVME") || nameUpper.Contains("SSD"))
            return ("Накопители / NVMe", "💾");

        if (cls.Contains("DISPLAY") || nameUpper.Contains("GEFORCE") || nameUpper.Contains("RADEON") || nameUpper.Contains("INTEL GRAPHICS"))
            return ("Видеокарты (GPU)", "🎮");

        if (cls.Contains("NET") || nameUpper.Contains("ETHERNET") || nameUpper.Contains("WI-FI") || nameUpper.Contains("WIRELESS"))
            return ("Сетевые адаптеры", "🌐");

        if (cls.Contains("SYSTEM") || cls.Contains("PROCESSOR") || cls.Contains("COMPUTER"))
            return ("Системные шины", "⚙");

        if (cls.Contains("USB"))
            return ("USB контроллеры", "🔌");

        if (cls.Contains("MEDIA") || cls.Contains("AUDIO"))
            return ("Аудиоустройства", "🔊");

        if (cls.Contains("KEYBOARD") || cls.Contains("MOUSE") || cls.Contains("HIDCLASS"))
            return ("Периферия / Ввод", "⌨");

        if (cls.Contains("BLUETOOTH"))
            return ("Bluetooth", "📶");

        if (cls.Contains("MONITOR"))
            return ("Мониторы", "🖥");

        if (cls.Contains("PRINTER") || cls.Contains("PRINT"))
            return ("Принтеры / Сканеры", "🖨");

        if (cls.Contains("BATTERY") || cls.Contains("POWER"))
            return ("Питание / Батареи", "🔋");

        if (cls.Contains("HUMAN") || cls.Contains("BIOMETRIC"))
            return ("Биометрия", "🔐");

        if (cls.Contains("NET "))
            return ("Сетевые адаптеры", "🌐");

        // Раньше здесь возвращался СЫРОЙ английский PNPClass (например
        // «DiskDrive», «Net», «System»). Такие значения не содержат русских
        // подстрок, по которым работают фильтры, поэтому устройства из
        // fallback-категории ИСЧЕЗАЛИ при активации любого категорийного
        // фильтра. Теперь используется гарантированная русская категория.
        return ("Прочие устройства", "🧩");
    }

    private void ApplyFilters()
    {
        if (DriversListView == null || _allDevices == null)
            return;

        string query = SearchBox?.Text?.Trim().ToLowerInvariant() ?? "";

        var filtered = _allDevices.Where(d =>
        {
            // Категориальный фильтр.
            // RadioButton'ы объединены в группу (GroupName="DeviceFilters"),
            // поэтому активен РОВНО ОДИН. Проверки по-прежнему расположены
            // последовательно, но условие «ни один активный фильтр не
            // отверг элемент» даёт корректную семантику даже при
            // программной установке нескольких флагов.
            bool rejected = false;
            if (FilterStorageRadio?.IsChecked == true && !d.CategoryName.Contains("Накопители")) rejected = true;
            if (FilterGpuRadio?.IsChecked == true && !d.CategoryName.Contains("Видеокарт")) rejected = true;
            if (FilterNetworkRadio?.IsChecked == true && !d.CategoryName.Contains("Сетев")) rejected = true;
            if (FilterSystemRadio?.IsChecked == true && !d.CategoryName.Contains("Системн")) rejected = true;
            if (FilterIssuesRadio?.IsChecked == true && !d.HasError) rejected = true;
            if (rejected) return false;

            // Поисковый запрос
            if (!string.IsNullOrEmpty(query))
            {
                return d.Name.ToLowerInvariant().Contains(query) ||
                       d.Manufacturer.ToLowerInvariant().Contains(query) ||
                       d.HardwareId.ToLowerInvariant().Contains(query) ||
                       d.ServiceName.ToLowerInvariant().Contains(query);
            }

            return true;
        }).ToList();

        DriversListView.ItemsSource = filtered;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilters();
    private void FilterRadio_Checked(object sender, RoutedEventArgs e) => ApplyFilters();

    private void OpenDeviceManager_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        try
        {
            Process.Start(new ProcessStartInfo("devmgmt.msc") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось открыть Диспетчер устройств: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Экранирует значение для CSV.
    ///
    /// <para>Две угрозы, обе устранены:</para>
    /// <list type="bullet">
    /// <item>Встроенные кавычки не экранировались, что ломало структуру файла.</item>
    /// <item>Ведущие =, +, −, @ заставляют Excel/LibreOffice трактовать ячейку
    /// как ФОРМУЛУ (CSV injection). Значения берутся из метаданных устройств,
    /// то есть именем устройства можно управлять локально.</item>
    /// </list>
    /// </summary>
    private static string CsvCell(string? value)
    {
        string v = value ?? "";

        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@')
        {
            v = "'" + v;
        }

        return "\"" + v.Replace("\"", "\"\"") + "\"";
    }

    private void ExportReport_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        var sfd = new SaveFileDialog
        {
            Title = "Экспорт отчёта по драйверам и оборудованию",
            Filter = "Текстовый отчёт (*.txt)|*.txt|Таблица CSV (*.csv)|*.csv",
            FileName = $"MotionCommander_Drivers_{DateTime.Now:yyyyMMdd_HHmm}.txt"
        };

        if (sfd.ShowDialog() == true)
        {
            var sb = new StringBuilder();
            bool isCsv = sfd.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);

            if (isCsv)
            {
                sb.AppendLine("Статус;Устройство;Категория;Производитель;Служба;КодОшибки;HardwareID");
                foreach (var d in _allDevices)
                {
                    sb.AppendLine(string.Join(';',
                        CsvCell(d.StatusBadgeText),
                        CsvCell(d.Name),
                        CsvCell(d.CategoryName),
                        CsvCell(d.Manufacturer),
                        CsvCell(d.ServiceName),
                        CsvCell(d.ConfigManagerErrorCode.ToString()),
                        CsvCell(d.HardwareId)));
                }
            }
            else
            {
                sb.AppendLine("===============================================================================");
                sb.AppendLine("                 MOTION COMMANDER — ОТЧЁТ ОБ ОБОРУДОВАНИИ И ДРАЙВЕРАХ           ");
                sb.AppendLine($" Дата создания: {DateTime.Now:dd.MM.yyyy HH:mm:ss}");
                sb.AppendLine($" Всего устройств: {_allDevices.Count}, с ошибками/отключено: {_allDevices.Count(d => d.HasError)}");
                sb.AppendLine("===============================================================================");
                sb.AppendLine();

                foreach (var d in _allDevices)
                {
                    sb.AppendLine($"[{d.StatusBadgeText}] {d.Name}");
                    sb.AppendLine($"  Категория:     {d.CategoryName} ({d.PNPClass})");
                    sb.AppendLine($"  Производитель: {d.Manufacturer}");
                    sb.AppendLine($"  Служба:        {d.ServiceName}");
                    sb.AppendLine($"  Состояние:     {d.ErrorDescription}");
                    sb.AppendLine($"  Hardware ID:   {d.HardwareId}");
                    sb.AppendLine(new string('-', 60));
                }
            }

            // Раньше запись шла БЕЗ try/catch: отказ в доступе (например,
            // защищённая папка), переполнение диска или перехват пути другим
            // процессом приводили к фатальному крашу прямо из обработчика кнопки.
            try
            {
                File.WriteAllText(sfd.FileName, sb.ToString(), Encoding.UTF8);
                MessageBox.Show($"Отчёт успешно сохранён:\n{sfd.FileName}", "Экспорт завершён", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось сохранить отчёт:\n\n{ex.Message}", "Ошибка записи", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
