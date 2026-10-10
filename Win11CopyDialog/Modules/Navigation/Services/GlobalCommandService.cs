using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Win11CopyDialog.Modules.Navigation.Services;

public enum SearchItemCategory
{
    Tool,
    Scenario,
    Drive,
    Folder,
    File
}

public sealed class GlobalSearchResultItem
{
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public SearchItemCategory Category { get; set; } = SearchItemCategory.Tool;
    public string Icon { get; set; } = "⚡";
    public Action? ExecuteAction { get; set; }
}

public sealed class GlobalCommandService
{
    private static readonly Lazy<GlobalCommandService> _instance = new(() => new GlobalCommandService());
    public static GlobalCommandService Instance => _instance.Value;

    private readonly List<GlobalSearchResultItem> _staticItems = new();

    public event Action<string>? RequestNavigateFolder;
    public event Action<int>? RequestSwitchTab;
    public event Action<int>? RequestSwitchStorageSubTab;
    public event Action? RequestOpenQueue;
    public event Action? RequestOpenDownloads;
    public event Action? RequestOpenUninstaller;
    public event Action? RequestOpenWizTree;
    public event Action? RequestOpenDuplicates;
    public event Action? RequestOpenDrivers;
    public event Action? RequestQuickCleanup;
    public event Action? RequestPrepareCopy;

    private GlobalCommandService()
    {
        BuildStaticCatalog();
    }

    private void BuildStaticCatalog()
    {
        _staticItems.Add(new GlobalSearchResultItem
        {
            Title = "Очередь операций (Unified Queue)",
            Subtitle = "Централизованный мониторинг копирования, загрузок, архивации и сканирования",
            Category = SearchItemCategory.Tool,
            Icon = "⏳",
            ExecuteAction = () => RequestOpenQueue?.Invoke()
        });

        _staticItems.Add(new GlobalSearchResultItem
        {
            Title = "Сценарий: 🧹 Освободить место",
            Subtitle = "Экспресс-сканирование и безопасная очистка временных файлов и кэша",
            Category = SearchItemCategory.Scenario,
            Icon = "🧹",
            ExecuteAction = () => RequestQuickCleanup?.Invoke()
        });

        _staticItems.Add(new GlobalSearchResultItem
        {
            Title = "Сценарий: 🚀 Подготовить диск к копированию",
            Subtitle = "Проверка свободного места, выполнение TRIM/ReTrim и настройка I/O буферов",
            Category = SearchItemCategory.Scenario,
            Icon = "🚀",
            ExecuteAction = () => RequestPrepareCopy?.Invoke()
        });

        _staticItems.Add(new GlobalSearchResultItem
        {
            Title = "Бенчмарк скорости дисков",
            Subtitle = "Стресс-тест чтения/записи Direct I/O без системного кэша Windows",
            Category = SearchItemCategory.Tool,
            Icon = "🏎",
            ExecuteAction = () => { RequestSwitchTab?.Invoke(2); RequestSwitchStorageSubTab?.Invoke(2); }
        });

        _staticItems.Add(new GlobalSearchResultItem
        {
            Title = "Диагностика и оптимизация системы",
            Subtitle = "Мониторинг CPU, памяти, дисков в динамике, детекция Bottleneck и профили",
            Category = SearchItemCategory.Tool,
            Icon = "📊",
            ExecuteAction = () => RequestSwitchTab?.Invoke(3)
        });

        _staticItems.Add(new GlobalSearchResultItem
        {
            Title = "Менеджер установленных программ",
            Subtitle = "Удаление программ, поиск скрытых остатков, пакетное удаление и анализ веса",
            Category = SearchItemCategory.Tool,
            Icon = "🗑",
            ExecuteAction = () => RequestOpenUninstaller?.Invoke()
        });

        _staticItems.Add(new GlobalSearchResultItem
        {
            Title = "Менеджер загрузок (Download Engine)",
            Subtitle = "Многопоточная загрузка файлов из сети с поддержкой докачки",
            Category = SearchItemCategory.Tool,
            Icon = "🌐",
            ExecuteAction = () => RequestOpenDownloads?.Invoke()
        });

        _staticItems.Add(new GlobalSearchResultItem
        {
            Title = "Анализатор дискового пространства (WizTree)",
            Subtitle = "Интерактивная карта распределения свободного и занятого места",
            Category = SearchItemCategory.Tool,
            Icon = "🌳",
            ExecuteAction = () => RequestOpenWizTree?.Invoke()
        });

        _staticItems.Add(new GlobalSearchResultItem
        {
            Title = "Поиск и удаление дубликатов",
            Subtitle = "3-этапный поиск одинаковых файлов (размер, пре-хэш, SHA-256)",
            Category = SearchItemCategory.Tool,
            Icon = "🔍",
            ExecuteAction = () => RequestOpenDuplicates?.Invoke()
        });

        _staticItems.Add(new GlobalSearchResultItem
        {
            Title = "Инспектор драйверов и оборудования",
            Subtitle = "Проверка состояния накопителей NVMe, шины PCIe, видеокарты и контроллеров",
            Category = SearchItemCategory.Tool,
            Icon = "🧩",
            ExecuteAction = () => RequestOpenDrivers?.Invoke()
        });

        _staticItems.Add(new GlobalSearchResultItem
        {
            Title = "Оптимизация накопителей и TRIM",
            Subtitle = "Принудительный вызов ReTrim для SSD и дефрагментация магнитных HDD",
            Category = SearchItemCategory.Tool,
            Icon = "⚡",
            ExecuteAction = () => { RequestSwitchTab?.Invoke(2); RequestSwitchStorageSubTab?.Invoke(3); }
        });

        _staticItems.Add(new GlobalSearchResultItem
        {
            Title = "Здоровье дисков S.M.A.R.T.",
            Subtitle = "Атрибуты износа, температура, переназначенные секторы и прогноз надежности",
            Category = SearchItemCategory.Tool,
            Icon = "🩺",
            ExecuteAction = () => { RequestSwitchTab?.Invoke(2); RequestSwitchStorageSubTab?.Invoke(0); }
        });

        _staticItems.Add(new GlobalSearchResultItem
        {
            Title = "Менеджер разделов (Partition Manager)",
            Subtitle = "Форматирование, смена буквы диска, метки тома и инспекция разделов",
            Category = SearchItemCategory.Tool,
            Icon = "🗂",
            ExecuteAction = () => { RequestSwitchTab?.Invoke(2); RequestSwitchStorageSubTab?.Invoke(1); }
        });
    }

    public List<GlobalSearchResultItem> Search(string query, string currentFolder = "")
    {
        var results = new List<GlobalSearchResultItem>();
        string q = (query ?? "").Trim();

        if (string.IsNullOrEmpty(q))
        {
            // Показываем популярные инструменты и сценарии
            return _staticItems.Take(8).ToList();
        }

        // 1. Поиск по каталогу инструментов и сценариев
        foreach (var it in _staticItems)
        {
            if (it.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                it.Subtitle.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(it);
            }
        }

        // 2. Поиск по дискам
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (!d.IsReady) continue;
                string label = string.IsNullOrEmpty(d.VolumeLabel) ? "Локальный диск" : d.VolumeLabel;
                if (d.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || label.Contains(q, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new GlobalSearchResultItem
                    {
                        Title = $"Диск {d.Name} ({label})",
                        Subtitle = $"Свободно: {d.AvailableFreeSpace / (1024 * 1024 * 1024)} ГБ из {d.TotalSize / (1024 * 1024 * 1024)} ГБ",
                        Category = SearchItemCategory.Drive,
                        Icon = "💽",
                        ExecuteAction = () => RequestNavigateFolder?.Invoke(d.RootDirectory.FullName)
                    });
                }
            }
        }
        catch { }

        // 3. Поиск по текущей папке
        if (!string.IsNullOrEmpty(currentFolder) && Directory.Exists(currentFolder))
        {
            try
            {
                var dir = new DirectoryInfo(currentFolder);
                foreach (var sub in dir.EnumerateDirectories($"*{q}*").Take(5))
                {
                    results.Add(new GlobalSearchResultItem
                    {
                        Title = $"Папка: {sub.Name}",
                        Subtitle = sub.FullName,
                        Category = SearchItemCategory.Folder,
                        Icon = "📁",
                        ExecuteAction = () => RequestNavigateFolder?.Invoke(sub.FullName)
                    });
                }

                foreach (var f in dir.EnumerateFiles($"*{q}*").Take(5))
                {
                    results.Add(new GlobalSearchResultItem
                    {
                        Title = $"Файл: {f.Name}",
                        Subtitle = $"{f.Length / 1024} КБ • {f.FullName}",
                        Category = SearchItemCategory.File,
                        Icon = "📄",
                        ExecuteAction = () => RequestNavigateFolder?.Invoke(f.DirectoryName ?? currentFolder)
                    });
                }
            }
            catch { }
        }

        return results;
    }
}
