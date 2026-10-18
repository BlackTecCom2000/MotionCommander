using System.IO;
using Microsoft.Win32;
using Win11CopyDialog.Modules.TajArena.Models;

namespace Win11CopyDialog.Modules.TajArena.Services;

/// <summary>
/// Результат поиска установленных игр.
/// </summary>
public sealed class DiscoveryResult
{
    public List<GameEntry> Games { get; set; } = new();

    /// <summary>
    /// Что проверено и что не нашлось — честный журнал, попадающий
    /// в статус окна. Без него пустой список неотличим от сбоя.
    /// </summary>
    public List<string> Probes { get; set; } = new();
}

/// <summary>
/// Автопоиск установленных игр: реестр установленных программ,
/// файл библиотек Steam и файл установок Epic Games.
/// </summary>
/// <remarks>
/// <para>Все три источника читаются «как есть». Ничего не
/// предполагается: отсутствующий файл означает «источника нет»,
/// а не «игр ноль». Неотфильтрованное исключение при чтении одного
/// узла реестра не останавливает весь поиск": узел пропускается,
/// причина пишется в <see cref="DiscoveryResult.Probes"/>.</para>
///
/// <para>Фильтр генераторов результатов строго белый: он
/// пропускает только записи, у которых платформа опознаётся
/// однозначно (отписывается Steam/Epic/Riot/GOG в названии,
/// издателе или путе). Список без фильтра подкладывал бы под
/// кнопкой «Играть» антивирусы и офисные пакеты.</para>
///
/// <para>Интерфейс Uninstaller знает те же ключи реестра, и это
/// осознанное дублирование: модуль Uninstaller про удаление, здесь
/// — про игры, и у них разные фильтры. Общее хранилище списка
/// связало бы две независимые функции.</para>
/// </remarks>
public static class GameDiscoveryService
{
    /// <summary>
    /// Слово-маркер в имени ключа реестра: Steam нумерует установленные игры.
    /// </summary>
    private const string SteamKeyPrefix = "Steam App";

    /// <summary>
    /// Слова в названии установленных программ, которые почётно
    /// попадают под признаки платформы (Steam/Epic/Riot/GOG), но
    /// играми не являются: утилиты и сопутствующие пакеты платформ.
    /// </summary>
    private static readonly string[] Exclusions =
    {
        "uninstall", "redistributable", "runtime", "directx", "dotnet",
        "vc++", "vcredist", "sdk", "editor", "steam client", "steamworks",
        "epic games launcher", "riot client"
    };

    /// <summary>
    /// Стандартные структуры библиотек Steam: рядом с самой программой
    /// и на отдельном диске-хранилище.
    /// </summary>
    private static readonly string[] SteamLibraryPatterns =
    {
        "SteamLibrary\\steamapps\\common",
        "Program Files (x86)\\Steam\\steamapps\\common",
        "Program Files\\Steam\\steamapps\\common",
        "Steam\\steamapps\\common"
    };

    /// <summary>
    /// Стандартный путь файла установок в реестре Epic Games.
    /// </summary>
    private static readonly string[] EpicDataPaths =
    {
        Path.Combine("C:\\ProgramData", "Epic Games", "UnrealEngineLauncher", "LauncherInstalled.dat"),
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic Games", "UnrealEngineLauncher", "LauncherInstalled.dat")
    };

    /// <summary>
    /// Полностью синхронный поиск: маленький список, ноль будущих задач.
    /// </summary>
    public static DiscoveryResult Discover()
    {
        var result = new DiscoveryResult();
        var merged = new Dictionary<string, GameEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in FromRegistry(result))
        {
            merged[entry.InstallPath] = entry;
        }

        foreach (var entry in FromSteamLibraries(result))
        {
            // Реестр содержит более точное название игры из установщика,
            // поэтому запись из папки Steam не перетирает его.
            if (!merged.ContainsKey(entry.InstallPath)) merged[entry.InstallPath] = entry;
        }

        foreach (var entry in FromEpicDat(result))
        {
            if (!merged.ContainsKey(entry.InstallPath)) merged[entry.InstallPath] = entry;
        }

        foreach (var entry in FromRiotFolders(result))
        {
            if (!merged.ContainsKey(entry.InstallPath)) merged[entry.InstallPath] = entry;
        }

        foreach (var entry in merged.Values)
        {
            entry.CandidateExePaths = FindCandidateExecutables(entry.InstallPath, result);
            entry.MainExePath ??= PickDefaultExecutable(entry.CandidateExePaths, entry.InstallPath);
        }

        result.Games = merged.Values
            .OrderBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return result;
    }

    /// <summary>
    /// Реальный uninstall-реестр двух битностей и всех кустов.
    /// Возвращает список, а не итератор: внутри цикла try/catch,
    /// и итератор не может выдавать значения из такого блока.
    /// </summary>
    private static List<GameEntry> FromRegistry(DiscoveryResult result)
    {
        var accepted = new List<GameEntry>();
        var targets = new (RegistryHive Hive, RegistryView View, string Root)[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64, "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall"),
            (RegistryHive.CurrentUser, RegistryView.Default, "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall")
        };

        int totalKeys = 0;
        int rejected = 0;

        foreach (var (hive, view, root) in targets)
        {
            RegistryKey? baseKey = null;
            try
            {
                baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstallKey = baseKey.OpenSubKey(root);
                if (uninstallKey == null)
                {
                    result.Probes.Add($"реестр {hive}/{view}: раздел не открыт");
                    continue;
                }

                foreach (var subName in uninstallKey.GetSubKeyNames())
                {
                    totalKeys++;
                    try
                    {
                        using var sub = uninstallKey.OpenSubKey(subName);
                        if (sub == null) continue;

                        var (entry, rejectionReason) = FromRegistryKey(sub, subName);
                        if (entry == null)
                        {
                            rejected++;
                            // Причину печатаем только для ключей, у которых
                            // название ключа похоже на платформу: иначе
                            // журнал захлебнулся бы в сотнях обычных программ.
                            if (rejectionReason != null && PlatformWordIn(subName))
                            {
                                result.Probes.Add($"реестр: отказано «{subName}»: {rejectionReason}");
                            }
                        }
                        else
                        {
                            // Уникальность проверит словарь по InstallPath.
                            accepted.Add(entry);
                        }
                    }
                    catch (Exception ex)
                    {
                        result.Probes.Add($"реестр {subName}: {ex.GetType().Name}");
                    }
                }
            }
            catch (Exception ex)
            {
                result.Probes.Add($"реестр {hive}/{view}: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
            }
            finally
            {
                baseKey?.Dispose();
            }
        }

        result.Probes.Add($"реестр: прочитано {totalKeys} записей, игр опознано {accepted.Count}, отфильтровано {rejected}");

        return accepted;
    }

    /// <summary>
    /// Извлекает игру из одного ключа uninstall-реестра.
    /// Возвращает пару: запись и причину отказа. Причина — не null
    /// только у записей, которые «похожи на игру, но не приняты»:
    /// по ним журнал отвечает, почему конкретный ключ не попал в список.
    /// </summary>
    private static (GameEntry? Entry, string? RejectionReason) FromRegistryKey(
        RegistryKey sub, string keyName)
    {
        string displayName = sub.GetValue("DisplayName") as string ?? "";
        string publisher = sub.GetValue("Publisher") as string ?? "";
        string install = sub.GetValue("InstallLocation") as string ?? "";
        string icon = sub.GetValue("DisplayIcon") as string ?? "";
        string uninstall = sub.GetValue("UninstallString") as string ?? "";

        // Без пути установки нет и игры: после переустановок ключи
        // остаются, а папки исчезают. Нет папки — нет и размера, и запуска.
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return (null, PlatformWordIn(keyName) ? "нет DisplayName" : null);
        }

        var matcher = $"{displayName}\n{publisher}\n{keyName}\n{install}\n{icon}".ToLowerInvariant();

        bool looksSteam =
            matcher.Contains("steam") ||
            keyName.StartsWith(SteamKeyPrefix, StringComparison.OrdinalIgnoreCase) ||
            install.Contains("steamapps", StringComparison.OrdinalIgnoreCase) ||
            uninstall.Contains("steamapps", StringComparison.OrdinalIgnoreCase);

        bool looksEpic =
            publisher.Contains("epic games", StringComparison.OrdinalIgnoreCase) ||
            install.Contains("\\epic games\\", StringComparison.OrdinalIgnoreCase) ||
            uninstall.Contains("epic games", StringComparison.OrdinalIgnoreCase);

        bool looksRiot =
            publisher.Contains("riot games", StringComparison.OrdinalIgnoreCase) ||
            install.Contains("\\riot games\\", StringComparison.OrdinalIgnoreCase);

        bool looksGoG =
            publisher.Contains("gog", StringComparison.OrdinalIgnoreCase) ||
            matcher.Contains("gog galaxy", StringComparison.OrdinalIgnoreCase);

        if (!looksSteam && !looksEpic && !looksRiot && !looksGoG)
        {
            return (null, null);
        }

        if (string.IsNullOrWhiteSpace(install))
        {
            return (null, "нет InstallLocation");
        }

        if (!Directory.Exists(install))
        {
            return (null, $"папка установки не существует: {install}");
        }

        // Утилиты платформ сами по себе не игры: клиент Steam или
        // лаунчер Epic устанавливаются как обычные программы, и в
        // списке игр были бы мусором с реальными весами.
        if (IsExcludedName(displayName))
        {
            return (null, "служебное название платформы, а не игра");
        }

        var platform = looksSteam ? GamePlatform.Steam
            : looksEpic ? GamePlatform.EpicGames
            : looksRiot ? GamePlatform.RiotGames
            : GamePlatform.GoG;

        return (new GameEntry
        {
            DisplayName = displayName.Trim(),
            InstallPath = NormalizePath(install),
            Platform = platform
        }, null);
    }

    /// <summary>
    /// Есть ли в названии ключа реестра слово платформы: используется
    /// для отбора ключей, по которым журнал обязан объяснить отказ.
    /// </summary>
    private static bool PlatformWordIn(string keyName)
    {
        string lower = keyName.ToLowerInvariant();
        return lower.Contains("steam") || lower.Contains("epic") ||
               lower.Contains("riot") || lower.Contains("gog") ||
               lower.Contains("game");
    }

    /// <summary>
    /// Название — не игра, а служебный пакет платформы. Общий фильтр
    /// для всех трёх источников Steam-списка: без него манифест бы
    /// пронёс в список «Steamworks Common Redistributables» —
    /// реальный случай, пойманный пробой на живой машине.
    /// </summary>
    private static bool IsExcludedName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return true;

        string lower = displayName.ToLowerInvariant();
        return Exclusions.Any(bad => lower.Contains(bad));
    }

    /// <summary>
    /// Папки Steam: сначала стандартные схемы размещения на каждом
    /// диске и библиотеки из libraryfolders.vdf, затем — манифесты
    /// установленных приложений, читаемые прямо Valve-файлами.
    /// </summary>
    /// <remarks>
    /// <para>Возвращает список, а не итератор: парсинг libraryfolders.vdf
    /// окружён try/catch, и итератор не может выдавать значения из
    /// такого блока — это упирается в языковое ограничение CS1626.</para>
    ///
    /// <para>Манифесты appmanifest_*.acf нужны не для полноты ради:
    /// ключи реестра Steam хранят путь установки на момент установки.
    /// Если игру потом переносили на другой диск, реестровый путь ведёт
    /// в несуществующую папку, и запись отбрасывается. Манифест лежит
    /// в реальной библиотеке и всегда указывает актуальный каталог
    /// через поле «installdir» — это официальный источник Valve.</para>
    /// </remarks>
    private static List<GameEntry> FromSteamLibraries(DiscoveryResult result)
    {
        var accepted = new List<GameEntry>();
        var commonRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;

            foreach (var pattern in SteamLibraryPatterns)
            {
                var root = CombineRoot(drive.Name, pattern);
                if (Directory.Exists(root)) commonRoots.Add(NormalizePath(root));
            }
        }

        // libraryfolders.vdf содержит дополнительно смонтированные
        // библиотеки в нестандартных путях. Прямо парсим: файл это
        // простой «"path"\t\t"диск"», сложный парсер не нужен.
        var libraryFile = FindSteamLibraryFile();
        if (libraryFile != null)
        {
            try
            {
                var text = File.ReadAllText(libraryFile);
                var matches = System.Text.RegularExpressions.Regex.Matches(
                    text, "\"path\"\\s+\"([^\"]+)\"");
                foreach (System.Text.RegularExpressions.Match m in matches)
                {
                    var unparsedPath = m.Groups[1].Value.Replace("\\\\", "\\");
                    var commonPath = Path.Combine(unparsedPath, "steamapps", "common");
                    if (Directory.Exists(commonPath))
                    {
                        commonRoots.Add(NormalizePath(commonPath));
                    }
                }
                result.Probes.Add($"Steam libraryfolders.vdf: найден в {libraryFile}");
            }
            catch (Exception ex)
            {
                result.Probes.Add($"Steam libraryfolders.vdf: {ex.GetType().Name}");
            }
        }
        else
        {
            result.Probes.Add("Steam libraryfolders.vdf: файл не найден");
        }

        var uniqueFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int manifestFolders = 0;
        int manifestRejectedTotal = 0;

        foreach (var commonRoot in commonRoots.OrderBy(r => r, StringComparer.OrdinalIgnoreCase))
        {
            // Манифесты первыми: их имена — официальные имена от Valve.
            // Скан по exe-файлам дополняет список лишь папками без
            // манифеста и не должен перетирать правильные названия.
            var (manifestGames, manifestRejected) = FromSteamAppManifests(commonRoot, result);
            manifestRejectedTotal += manifestRejected;
            foreach (var entry in manifestGames)
            {
                manifestFolders++;
                if (uniqueFolders.Add(entry.InstallPath)) accepted.Add(entry);
            }

            foreach (var entry in FromSteamFolder(commonRoot))
            {
                if (uniqueFolders.Add(entry.InstallPath)) accepted.Add(entry);
            }
        }

        result.Probes.Add(
            $"Steam-библиотеки: корней {commonRoots.Count}, игровых папок по exe-файлам " +
            $"{accepted.Count - manifestFolders}, по манифестам {manifestFolders}, " +
            $"манифестов отклонено {manifestRejectedTotal}");

        return accepted;
    }

    /// <summary>
    /// Официальные манифесты установленных приложений Valve:
    /// steamapps\appmanifest_*.acf с полями «name» и «installdir».
    /// </summary>
    /// <remarks>
    /// Формат стабильный, поля читаются построчно регуляркой: файл не
    /// объект JSON, а Valve-структура («ключ"\t\t"значение»).
    /// Непрочитанный манифест фиксируется в журнале (result.Probes),
    /// а счётчик отказов возвращается вместе со списком: у итератора
    /// не может быть параметра ref, но счёт нужен для честного итога.
    /// </remarks>
    private static (List<GameEntry> Games, int Rejected) FromSteamAppManifests(
        string commonRoot, DiscoveryResult result)
    {
        var accepted = new List<GameEntry>();
        var rejected = 0;

        var steamApps = Path.Combine(Path.GetDirectoryName(commonRoot) ?? "", "");
        if (!Directory.Exists(steamApps)) return (accepted, rejected);

        string[] manifests;
        try
        {
            manifests = Directory.EnumerateFiles(steamApps, "appmanifest_*.acf").ToArray();
        }
        catch
        {
            return (accepted, rejected);
        }

        foreach (var manifest in manifests)
        {
            string name, installDir;

            try
            {
                var text = File.ReadAllText(manifest);
                var nameMatch = System.Text.RegularExpressions.Regex.Match(
                    text, "\"name\"\\s+\"([^\"]+)\"");
                var dirMatch = System.Text.RegularExpressions.Regex.Match(
                    text, "\"installdir\"\\s+\"([^\"]+)\"");

                if (!nameMatch.Success || !dirMatch.Success)
                {
                    rejected++;
                    result.Probes.Add(
                        $"Steam манифест: не найдены поля в {Path.GetFileName(manifest)}");
                    continue;
                }

                name = nameMatch.Groups[1].Value;
                installDir = dirMatch.Groups[1].Value;
            }
            catch (Exception ex)
            {
                rejected++;
                result.Probes.Add(
                    $"Steam манифест: {Path.GetFileName(manifest)}: {ex.GetType().Name}");
                continue;
            }

            // Манифест лежит в реальной библиотеке: «installdir» всегда
            // относительно «common» рядом с steamapps.
            var gameDir = NormalizePath(Path.Combine(commonRoot, installDir));
            if (!Directory.Exists(gameDir))
            {
                rejected++;
                result.Probes.Add(
                    $"Steam манифест: папка «{installDir}» не существует (манифест {Path.GetFileName(manifest)})");
                continue;
            }

            // Отклонять папку из-за отсутствия exe нельзя: в манифесте
            // уже есть имя игры от Valve, а exe бывает на третьем
            // уровне (Counter-Strike 2: game\bin\win64\cs2.exe).
            // Но служебные пакеты («Steamworks Common Redistributables»)
            // фильтр отсекает по имени.
            if (IsExcludedName(name))
            {
                rejected++;
                result.Probes.Add(
                    $"Steam манифест: «{name}» — служебное название платформы, не игра");
                continue;
            }

            accepted.Add(new GameEntry
            {
                DisplayName = name,
                InstallPath = gameDir,
                Platform = GamePlatform.Steam
            });
        }

        return (accepted, rejected);
    }

    /// <summary>
    /// Игровыми считаются подпапки steamapps\common, у которых есть
    /// хотя бы один исполняемый файл в первых трёх уровнях.
    /// </summary>
    private static IEnumerable<GameEntry> FromSteamFolder(string commonRoot)
    {
        if (!Directory.Exists(commonRoot)) yield break;

        string[] candidates;
        try
        {
            candidates = Directory.EnumerateDirectories(commonRoot).ToArray();
        }
        catch
        {
            // Папка не открылась (нет прав, повреждённая точка монтирования):
            // одна нерабочая библиотека не должна прерывать поиск.
            yield break;
        }

        foreach (var dir in candidates)
        {
            // Имя папки — как правило, имя игры, но часть папок Steam
            // — служебные («Steamworks Shared»): их отсекает общий
            // фильтр исключений.
            var folderName = Path.GetFileName(dir);
            if (IsExcludedName(folderName)) continue;
            if (!IsLikelyGameFolder(dir)) continue;

            yield return new GameEntry
            {
                DisplayName = folderName ?? dir,
                InstallPath = dir,
                Platform = GamePlatform.Steam
            };
        }
    }

    /// <summary>
    /// Файл установок Epic Games: единственный источник с точной
    /// структурой «имя — путь — размер», без интерпретации папок.
    /// </summary>
    private static IEnumerable<GameEntry> FromEpicDat(DiscoveryResult result)
    {
        string? dataPath = EpicDataPaths.FirstOrDefault(File.Exists);
        if (dataPath == null)
        {
            result.Probes.Add("Epic Games LauncherInstalled.dat: не найден");
            yield break;
        }

        result.Probes.Add($"Epic Games LauncherInstalled.dat: есть ({dataPath})");

        string[] entries;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(
                File.ReadAllText(dataPath),
                new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = true });

            entries = doc.RootElement.GetProperty("InstallationList")
                .EnumerateArray()
                .Select(i => i.GetProperty("InstallLocation").GetString() ?? "")
                .ToArray();
        }
        catch (Exception ex)
        {
            // Файл есть, но прочитать не удалось — это тоже результат,
            // а не пустой список: иначе неясно, чья это ошибка.
            result.Probes.Add($"Epic Games LauncherInstalled.dat: {ex.GetType().Name}");
            yield break;
        }

        int epicCount = 0;
        foreach (var install in entries)
        {
            if (string.IsNullOrWhiteSpace(install) || !Directory.Exists(install)) continue;
            if (!IsLikelyGameFolder(install)) continue;

            var epicName = Path.GetFileName(install.TrimEnd('\\')) ?? install;
            if (IsExcludedName(epicName)) continue;

            epicCount++;
            yield return new GameEntry
            {
                DisplayName = epicName,
                InstallPath = NormalizePath(install),
                Platform = GamePlatform.EpicGames
            };
        }

        result.Probes.Add($"Epic Games: папок найдено {epicCount}");
    }

    /// <summary>
    /// Riot Games: редактирует созданные программы в реестре
    /// и складывает их в стандартную папку «Riot Games» на диске.
    /// </summary>
    private static IEnumerable<GameEntry> FromRiotFolders(DiscoveryResult result)
    {
        string[] roots =
        {
            "C:\\Riot Games",
        };

        int riot = 0;
        foreach (var dir in from d in DriveInfo.GetDrives()
                            where d.DriveType == DriveType.Fixed && d.IsReady
                            select Path.Combine(d.Name, "Riot Games"))
        {
            if (!Directory.Exists(dir)) continue;
            result.Probes.Add($"Riot Games: папка найдена: {dir}");
            foreach (var entry in FromSteamFolder(dir))
            {
                riot++;
                yield return new GameEntry
                {
                    DisplayName = entry.DisplayName,
                    InstallPath = entry.InstallPath,
                    Platform = GamePlatform.RiotGames
                };
            }
        }

        if (riot == 0) result.Probes.Add("Riot Games: папки не найдено");
        else result.Probes.Add($"Riot Games: игр найдено {riot}");
    }

    /// <summary>
    /// Папка считается игрой, если в первых трёх уровнях есть
    /// исполняемый файл. Без этого условия списком заполнились бы
    /// инсталляторы-остатки и пустые каталоги после удаления.
    /// Глубина три, а не два: у Counter-Strike 2 стартовый exe
    /// лежит на третьем уровне (game\bin\win64\cs2.exe).
    /// </summary>
    public static bool IsLikelyGameFolder(string dir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.exe",
                new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = true,
                    MaxRecursionDepth = 3
                }))
            {
                if (!IsExecutableNoise(Path.GetFileName(f))) return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// Системные и служебные исполняемые файлы, которые являются
    /// шумом и никогда не бывают «главной» программой игры.
    /// </summary>
    private static bool IsExecutableNoise(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return true;

        string lower = fileName.ToLowerInvariant();

        string[] noise =
        {
            "uninstall", "uninst", "setup", "installe", "redist",
            "vcredist", "dxsetup", "dotnet", "crash", "handler",
            "updater", "unitycrash", "crashhandler", "eula",
            "copyright", "readme", "startgame\"", "directx",
            "\"launchhelper", "installscript", "bspatch", "patcher"
        };

        return noise.Any(n => lower.Contains(n));
    }

    /// <summary>
    /// Список кандидатов на главный исполняемый файл: отсортирован по
    /// размеру от большего к меньшему, с осмысленными префиксами.
    /// </summary>
    private static List<string> FindCandidateExecutables(string installPath, DiscoveryResult result)
    {
        try
        {
            var exes = Directory.EnumerateFiles(installPath, "*.exe",
                    new EnumerationOptions
                    {
                        IgnoreInaccessible = true,
                        RecurseSubdirectories = true,
                        MaxRecursionDepth = 3
                    })
                .Where(f => !IsExecutableNoise(Path.GetFileName(f)))
                .Select(f => new Tuple<string, long>(f, new FileInfo(f).Length))
                .OrderByDescending(t => t.Item2)
                .Select(t => t.Item1)
                .Take(8)
                .ToList();

            return exes;
        }
        catch (Exception ex)
        {
            result.Probes.Add($"кандидаты {Path.GetFileName(installPath)}: {ex.GetType().Name}");
            return new List<string>();
        }
    }

    /// <summary>
    /// Автовыбор запускаемого файла.
    /// Правило осознанно скромное: железная точность невозможна, и когда
    /// кандидатов несколько, выбрать однозначного нельзя — в этом
    /// случае возвращается null и решает пользователь.
    /// </summary>
    private static string? PickDefaultExecutable(List<string> candidates, string installPath)
    {
        if (candidates.Count == 0) return null;

        var folderName = Path.GetFileName(installPath.TrimEnd('\\')) ?? "";

        // 1) Точное совпадение имени папки игры и исполняемого файла.
        var exact = candidates.FirstOrDefault(f =>
            Path.GetFileName(f).Equals(Path.GetFileNameWithoutExtension(folderName) + ".exe",
                StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileNameWithoutExtension(f).Equals(folderName, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

        // 2) launcher в корне игры: Valve и Epic обычно называют
        // стартовый файл именно так, и это редко вызывает споры.
        var launcher = candidates.FirstOrDefault(f =>
        {
            string name = Path.GetFileName(f).ToLowerInvariant();
            string folder = Path.GetDirectoryName(f) ?? "";
            return (name.Contains("launcher") || name.Contains("game")) &&
                   string.Equals(folder, installPath, StringComparison.OrdinalIgnoreCase);
        });
        if (launcher != null) return launcher;

        // 3) Один-единственный exe без неоднозначности — выбираем его.
        if (candidates.Count == 1) return candidates[0];

        // Много кандидатов и нет особых совпадений: решать пользователю.
        return null;
    }

    /// <summary>Нормализация пути: убирает завершающие разделители.</summary>
    public static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path.TrimEnd('\\', '/')); }
        catch { return path.TrimEnd('\\', '/'); }
    }

    private static string CombineRoot(string driveRoot, string rest) => Path.Combine(driveRoot, rest);

    private static string? FindSteamLibraryFile()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;

            foreach (var pattern in new[] { "Program Files (x86)\\Steam", "Steam", "SteamLibrary" })
            {
                var candidate = Path.Combine(drive.Name, pattern, "steamapps", "libraryfolders.vdf");
                if (File.Exists(candidate)) return candidate;
            }
        }

        // Один-единственный случай: Steam может стоять в INET-папке.
        var x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrEmpty(x86))
        {
            var candidate = Path.Combine(x86, "Steam", "steamapps", "libraryfolders.vdf");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }
}
