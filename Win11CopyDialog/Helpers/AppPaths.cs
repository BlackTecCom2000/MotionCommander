using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Win11CopyDialog.Helpers;

public enum InstallMode
{
    /// <summary>Обычная установка в Program Files. Данные — в профиле пользователя.</summary>
    Installed = 0,
    /// <summary>Портативный запуск. Данные — рядом с программой (если папка доступна для записи).</summary>
    Portable = 1
}

/// <summary>
/// Единый источник правды по путям и режиму установки.
///
/// Проблема, которую решает: приложение писало изменяемое состояние
/// (staging автообновления, crash.log, app_state.json, бенчмарки) рядом с exe.
/// В портативном режиме это работает, а в C:\Program Files — нет: запись запрещена,
/// автообновление падало с "Access denied", а crash.log молча не создавался,
/// хотя диалог утверждал, что он записан.
///
/// Режим определяется тремя независимыми признаками (любого достаточно):
///   1. Файл install.json с "portable": true  — явный переключатель пользователя
///   2. unins000.exe рядом с exe               — установлено Inno Setup
///   3. Ключ реестра Inno Setup                — установлено, но exe перемещён
/// Ключ реестра и unins000.exе различают установленную копию от разобранной
/// просто распакованного архива.
/// </summary>
public static class AppPaths
{
    private const string AppFolderName = "MotionCommander";

    private static bool? _isPortableOverride;

    // ================= Режим установки =================

    /// <summary>Каталог с самим приложением (может быть только для чтения).</summary>
    public static string BaseDirectory { get; } = AppContext.BaseDirectory.TrimEnd('\\', '/');

    /// <summary>Каталог профиля пользователя (всегда доступен для записи).</summary>
    public static string UserDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppFolderName);

    /// <summary>Исполняемый файл приложения.</summary>
    public static string ExecutablePath { get; } = Environment.ProcessPath
        ?? Path.Combine(BaseDirectory, "Win11CopyDialog.exe");

    public static bool IsInstalled
    {
        get
        {
            if (_isPortableOverride.HasValue) return !_isPortableOverride.Value;
            return !IsPortable;
        }
    }

    public static bool IsPortable => _isPortableOverride ?? DetectPortable();

    public static InstallMode Mode => IsPortable ? InstallMode.Portable : InstallMode.Installed;

    public static string ModeDisplayName => IsPortable
        ? "Портативный режим (данные рядом с программой)"
        : "Полная установка (данные в профиле пользователя)";

    private static bool DetectPortable()
    {
        // 1. Явный переключатель пользователя — высший приоритет.
        string marker = Path.Combine(BaseDirectory, "install.json");
        try
        {
            if (File.Exists(marker))
            {
                var m = JsonSerializer.Deserialize<InstallMarker>(File.ReadAllText(marker));
                if (m?.Portable.HasValue == true) return m.Portable.Value;
            }
        }
        catch { /* повреждённый маркер — просто игнорируем */ }

        // 2. Признаки установки Inno Setup.
        if (File.Exists(Path.Combine(BaseDirectory, "unins000.exe"))) return false;
        if (InnoRegistryInstallDir() != null) return false;

        // 3. Ничего не найдено: считаем портативным только если каталог доступен для записи
        //    И мы не внутри Program Files. Иначе приложение не сможет работать как portable.
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        bool inProgramFiles =
            (!string.IsNullOrEmpty(pf) && BaseDirectory.StartsWith(pf, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(pf86) && BaseDirectory.StartsWith(pf86, StringComparison.OrdinalIgnoreCase));

        if (inProgramFiles) return false;
        return IsDirectoryWritable(BaseDirectory);
    }

    private static string? InnoRegistryInstallDir()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (key == null) return null;
            foreach (string sub in key.GetSubKeyNames())
            {
                using var app = key.OpenSubKey(sub);
                string? publisher = app?.GetValue("Publisher") as string;
                if (publisher is null || !publisher.Contains("BlackTecCom", StringComparison.OrdinalIgnoreCase)) continue;
                string? loc = app?.GetValue("InstallLocation") as string;
                if (!string.IsNullOrEmpty(loc) && loc.Contains("Motion", StringComparison.OrdinalIgnoreCase)) return loc;
            }
        }
        catch { /* реестр может быть недоступен — не критично */ }
        return null;
    }

    /// <summary>Включает или отключает портативный режим, создавая/удаляя install.json.</summary>
    public static bool SetPortable(bool portable, out string? error)
    {
        error = null;
        try
        {
            string marker = Path.Combine(BaseDirectory, "install.json");
            if (portable)
            {
                if (!IsDirectoryWritable(BaseDirectory))
                {
                    error = "Папка программы доступна только для чтения. " +
                            "Портативный режим можно включить только если программа не установлена в Program Files.";
                    return false;
                }
                File.WriteAllText(marker, JsonSerializer.Serialize(
                    new InstallMarker { Portable = true, Version = 1 },
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                if (File.Exists(marker)) File.Delete(marker);
            }
            _isPortableOverride = portable;
            _dataDirectory = null;   // каталог данных изменился — сбрасываем кэш
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Сбрасывает кэш определения режима и каталога данных.</summary>
    public static void ResetCaches()
    {
        _isPortableOverride = null;
        _dataDirectory = null;
    }

    private sealed class InstallMarker
    {
        public bool? Portable { get; set; }
        public int Version { get; set; }
    }

    // ================= Каталоги данных =================

    private static string? _dataDirectory;
    private static readonly object _dirLock = new();

    /// <summary>
    /// Каталог для изменяемого состояния: настройки, логи, staging автообновления, кэш.
    /// В портативном режиме — рядом с программой; иначе — в профиле пользователя.
    /// Если выбранный каталог недоступен для записи, автоматически используется профиль,
    /// чтобы приложение не падало на read-only носителе (флешке с защитой от записи).
    ///
    /// <para>Инициализация под блокировкой. Раньше стояло `??=`, что не является
    /// потокобезопасной ленивой инициализацией: ResolveDataDirectory() внутри
    /// выполняет НАСТОЯЩУЮ проверку доступности каталога, создавая пробный файл,
    /// то есть делает дисковый ввод-вывод. Два потока могли войти одновременно,
    /// и второй увидел бы незавершённое состояние.</para>
    /// </summary>
    public static string DataDirectory
    {
        get
        {
            var cached = Volatile.Read(ref _dataDirectory);
            if (cached != null) return cached;
            lock (_dirLock)
            {
                return _dataDirectory ??= ResolveDataDirectory();
            }
        }
    }

    private static string ResolveDataDirectory()
    {
        // Явное указание пользователем важнее автоматики: иначе
        // переопределение из настроек просто игнорировалось бы.
        if (!string.IsNullOrEmpty(_configuredDataDirectory))
            return _configuredDataDirectory;

        if (IsPortable && IsDirectoryWritable(BaseDirectory))
            return BaseDirectory;
        return UserDataDirectory;
    }

    /// <summary>Каталог, куда реально пишутся изменяемые файлы. Всегда доступен для записи.</summary>
    public static string WritableDataDirectory
    {
        get
        {
            string preferred = DataDirectory;
            if (IsDirectoryWritable(preferred)) return preferred;
            EnsureDir(UserDataDirectory);
            return UserDataDirectory;
        }
    }

    // ================= Именованные пути =================

    public static string SettingsFile => Path.Combine(WritableDataDirectory, "settings.json");
    public static string ThemeFile => Path.Combine(WritableDataDirectory, "theme.json");
    public static string CrashLogFile => Path.Combine(WritableDataDirectory, "crash.log");
    public static string AppStateFile => Path.Combine(WritableDataDirectory, "app_state.json");

    /// <summary>Staging автообновления. Раньше был рядом с exe — в Program Files это давало Access denied.</summary>
    public static string StagingDirectory => Path.Combine(WritableDataDirectory, "staging");

    public static string BenchmarkDirectory => Path.Combine(WritableDataDirectory, "benchmarks");
    public static string DownloadsDatabase => Path.Combine(WritableDataDirectory, "downloads.db");
    public static string ReportsDirectory => Path.Combine(WritableDataDirectory, "reports");
    public static string LogsDirectory => Path.Combine(WritableDataDirectory, "logs");

    /// <summary>
    /// Файл показателей последнего запуска.
    /// </summary>
    /// <remarks>
    /// Лежит рядом с настройками, а не рядом с программой: в Program
    /// Files запись запрещена, и метрики не сохранились бы вовсе.
    /// </remarks>
    public static string MetricsFile => Path.Combine(WritableDataDirectory, "startup-metrics.json");

    /// <summary>
    /// Файл падений программы.
    /// </summary>
    /// <remarks>
    /// Раньше лежал рядом с исполняемым файлом, и в Program Files
    /// запись была запрещена: лог молча не создавался, а диалог
    /// утверждал, что подробности записаны.
    /// </remarks>
    public static string CrashLogPath => CrashLogFile;
    public static string CacheDirectory => Path.Combine(WritableDataDirectory, "cache");

    /// <summary>Все каталоги создаются сразу, чтобы не спотыкаться о FileNotFound в разных местах.</summary>
    public static void EnsureDirectories()
    {
        // Каталог данных указывается явно, чтобы администратор мог его
        // заранее создать и положить туда, например, переносную базу.
        EnsureDir(ConfiguredDataDirectory);
        EnsureDir(WritableDataDirectory);
        foreach (var d in new[] { StagingDirectory, BenchmarkDirectory, ReportsDirectory, LogsDirectory, CacheDirectory })
            EnsureDir(d);
    }

    /// <summary>
    /// Каталог данных, указанный пользователем в настройках, либо пустая строка.
    /// </summary>
    /// <remarks>
    /// Хранится в settings.json, поэтому работает и для обычного запуска
    /// программы, и для инсталлятора (через ключ /DATA=). Пустое значение
    /// означает «использовать каталог по умолчанию», то есть профиль
    /// пользователя для обычной установки и папку программы для переносной.
    /// </remarks>
    public static string ConfiguredDataDirectory
    {
        get => _configuredDataDirectory ?? "";
        set
        {
            _configuredDataDirectory = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    private static string? _configuredDataDirectory;

    /// <summary>
    /// Читает переопределение каталога данных из файла настроек.
    /// </summary>
    /// <remarks>
    /// Вызывается один раз при старте. Раньше такой возможности не было
    /// вовсе: путь к данным определялся только автоматически по признаку
    /// переносной установки, и указать другой каталог было нечем.
    /// </remarks>
    public static void LoadDataDirectoryOverride()
    {
        try
        {
            string file = Path.Combine(UserDataDirectory, "settings.json");
            if (!File.Exists(file)) return;

            using var doc = System.Text.Json.JsonDocument.Parse(
                File.ReadAllText(file),
                new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = System.Text.Json.JsonCommentHandling.Skip });

            if (doc.RootElement.TryGetProperty("DataDirectory", out var el))
            {
                string? v = el.GetString();
                if (!string.IsNullOrWhiteSpace(v)) _configuredDataDirectory = v.Trim();
            }
        }
        catch
        {
            // Повреждённый файл настроек не должен мешать запуску программы.
        }
    }

    public static void EnsureDir(string path)
    {
        try { Directory.CreateDirectory(path); }
        catch { /* вызывающий код решает, критично ли это */ }
    }

    // ================= Проверки =================

    /// <summary>
    /// Проверяет возможность создать файл в каталоге, выполняя реальную запись.
    ///
    /// <para>Имя пробного файла включает GUID. Раньше туда подставлялся только
    /// Environment.ProcessId, поэтому ДВЕ одновременные проверки в одном
    /// процессе сталкивались: вторая получала IOException от FileMode.CreateNew,
    /// проглатывала его и возвращала false. Итог: приложение считало каталог
    /// недоступным для записи и молча уходило в другой путь данных.</para>
    /// </summary>
    public static bool IsDirectoryWritable(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return false;
            string probe = Path.Combine(path, $".write-probe-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
            using (var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
                fs.WriteByte(0);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Диагностика для окна «О программе»: что и почему определено как портативный режим.</summary>
    public static string Describe()
    {
        string marker = Path.Combine(BaseDirectory, "install.json");
        string reason =
            _isPortableOverride.HasValue ? "выбрано пользователем в настройках"
            : File.Exists(marker) ? "install.json: portable"
            : File.Exists(Path.Combine(BaseDirectory, "unins000.exe")) ? "unins000.exe (установка Inno Setup)"
            : InnoRegistryInstallDir() != null ? "запись в реестре (Inno Setup)"
            : IsPortable ? "каталог доступен для записи, признаков установки нет"
            : "каталог в Program Files или только для чтения";

        return $"Режим: {ModeDisplayName}\n" +
               $"Определён по: {reason}\n" +
               $"Программа: {BaseDirectory}\n" +
               $"Данные:   {WritableDataDirectory}\n" +
               $"Права:    {(IsDirectoryWritable(WritableDataDirectory) ? "запись разрешён" : "запись ЗАПРЕЩЕНА")}";
    }
}
