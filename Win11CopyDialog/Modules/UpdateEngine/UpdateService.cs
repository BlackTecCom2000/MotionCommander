using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using Win11CopyDialog.Helpers;

namespace Win11CopyDialog.Modules.UpdateEngine;

public enum VersionSwitchMode
{
    Upgrade,
    Downgrade,
    Reinstall
}

public enum UpdatePackageType
{
    Patch,
    FullPackage
}

public sealed class ReleaseVersionItem
{
    public string Version { get; set; } = "";
    public string TagName { get; set; } = "";
    public string Name { get; set; } = "";
    public string ReleaseDate { get; set; } = "";
    public string Body { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string PatchUrl { get; set; } = "";
    public string SetupExeUrl { get; set; } = "";
    public bool IsCurrent { get; set; }
    public bool IsUpgrade { get; set; }
    public bool IsDowngrade { get; set; }

    public string StatusBadgeText => IsCurrent ? "Текущая" : (IsUpgrade ? "Обновление" : "Откат");
    public string ActionButtonText => IsCurrent ? "Переустановить" : (IsUpgrade ? "Обновить" : "Откатить");
    public string DisplayTitle => $"Версия v{Version}" + (IsCurrent ? " (Установлена)" : "");
}

public sealed class UpdateInfo
{
    public string CurrentVersion { get; set; } = "3.0.0";
    public string LatestVersion { get; set; } = "3.0.0";
    public bool IsUpdateAvailable { get; set; }
    public string ReleaseDate { get; set; } = "";
    public List<string> Changelog { get; set; } = new();
    public string PatchUrl { get; set; } = "";
    public double PatchSizeMb { get; set; }
    public string PatchMinVersion { get; set; } = "3.8.0";

    /// <summary>
    /// Размер полного архива в МБ, как он указан в манифесте.
    /// </summary>
    /// <remarks>
    /// Ноль означает «манифест не сообщил размер» (например, ответ пришёл
    /// от GitHub API, а не от version.json). Раньше здесь стояло 40.5 —
    /// выдуманное число, которое показывалось пользователю как факт и
    /// расходилось с реальным размером архива после каждого релиза.
    /// </remarks>
    public double FullSizeMb { get; set; }
    public string DownloadUrl { get; set; } = "";
    public string InstallerUrl { get; set; } = "";
    public string SetupExeUrl { get; set; } = "";
    public string ErrorMessage { get; set; } = "";
    public VersionSwitchMode SwitchMode { get; set; } = VersionSwitchMode.Upgrade;
    public UpdatePackageType PackageType { get; set; } = UpdatePackageType.Patch;

    public bool HasPatch => !string.IsNullOrWhiteSpace(PatchUrl);

    public string GetEffectivePatchUrl()
    {
        if (!string.IsNullOrWhiteSpace(PatchUrl) && PatchUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return PatchUrl;
        return UpdateService.GetPatchZipUrl(LatestVersion);
    }

    public string GetEffectiveFullZipUrl()
    {
        if (!string.IsNullOrWhiteSpace(DownloadUrl) && DownloadUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return DownloadUrl;
        if (!string.IsNullOrWhiteSpace(InstallerUrl) && InstallerUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return InstallerUrl;
        return UpdateService.GetPortableZipUrl(LatestVersion);
    }

    /// <summary>
    /// Проверяет, применим ли быстрый патч к текущей установленной версии.
    /// </summary>
    public bool CanApplyPatch(string currentVersion)
    {
        if (SwitchMode != VersionSwitchMode.Upgrade) return false;
        if (string.IsNullOrWhiteSpace(LatestVersion)) return false;
        if (string.IsNullOrWhiteSpace(PatchMinVersion)) return true;
        return UpdateService.CompareVersions(currentVersion, PatchMinVersion) >= 0;
    }

    /// <summary>
    /// Фактический размер загрузки в МБ в зависимости от выбранного типа пакета.
    /// Ноль — размер неизвестен; выдуманное значение не подставляется.
    /// </summary>
    public double DownloadSizeMb => (PackageType == UpdatePackageType.Patch && HasPatch)
        ? PatchSizeMb
        : FullSizeMb;

    /// <summary>
    /// Процент экономии трафика при использовании патча по сравнению с полным пакетом.
    /// </summary>
    public int TrafficSavingPercent
    {
        get
        {
            if (PackageType != UpdatePackageType.Patch || PatchSizeMb <= 0) return 0;

            // Без размера полного архива экономию посчитать нельзя:
            // любая цифра здесь была бы выдумкой.
            double full = FullSizeMb;
            if (full <= PatchSizeMb) return 0;
            return (int)Math.Round((1.0 - (PatchSizeMb / full)) * 100.0);
        }
    }

    /// <summary>
    /// Честное описание пакета: тип, размер и экономия.
    /// </summary>
    public string FormattedPackageDescription
    {
        get
        {
            if (PackageType == UpdatePackageType.Patch && HasPatch)
            {
                int saving = TrafficSavingPercent;
                if (saving > 0)
                    return $"Быстрый патч: ~{PatchSizeMb:F1} МБ (экономия {saving}% трафика вместо ~{FullSizeMb:F1} МБ)";
                return $"Быстрый патч: ~{PatchSizeMb:F1} МБ";
            }

            return FullSizeMb > 0
                ? $"Полный пакет: ~{FullSizeMb:F1} МБ"
                : "Полный пакет: размер уточнится при загрузке";
        }
    }
}

public static class UpdateService
{
    /// <summary>Клиент для небольших запросов (манифест, GitHub API).</summary>
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    /// <summary>
    /// Клиент для скачивания пакетов обновления. Timeout бесконечный осознанно:
    /// HttpClient.Timeout действует на ВСЮ операцию, включая чтение тела ответа,
    /// поэтому прежние 15 секунд обрывали загрузку 38 МБ на каналах медленнее
    /// ~20 Мбит/с (а патч 3.5 МБ — медленнее ~1.9 Мбит/с) невнятным
    /// TaskCanceledException. Зависшее соединение теперь ловится отдельным
    /// сторожевым таймером простоя (60 с без единого байта), а не общим
    /// лимитом на весь файл.
    /// </summary>
    private static readonly HttpClient _downloadClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    public const string ManifestUrl = "https://raw.githubusercontent.com/BlackTecCom2000/MotionCommander/main/version.json";
    public const string GitHubReleasesUrl = "https://api.github.com/repos/BlackTecCom2000/MotionCommander/releases/latest";
    public const string GitHubRepoUrl = "https://github.com/BlackTecCom2000/MotionCommander";

    private const string RawBaseUrl = "https://raw.githubusercontent.com/BlackTecCom2000/MotionCommander/main/";
    private const string JsDelivrRepo = "https://cdn.jsdelivr.net/gh/BlackTecCom2000/MotionCommander";

    /// <summary>
    /// База зеркала jsDelivr для файлов без версии в имени (version.json).
    /// </summary>
    /// <remarks>
    /// Именно <c>@latest</c>, а не <c>@main</c>: ссылка на ветку кэшируется,
    /// и после релиза зеркало отдавало прошлый манифест — измерено
    /// 20.10.2026, @main → 3.8.51 при опубликованном 3.8.52. С @latest
    /// зеркало сразу отдало 3.8.52, а checksums.json — свежий.
    /// </remarks>
    private static string JsDelivrLatestBase => $"{JsDelivrRepo}@latest/";
    private const int DownloadAttemptsPerMirror = 3;
    private const int IdleTimeoutSeconds = 60;

    /// <summary>
    /// Адреса, с которых разрешено скачивать пакеты обновления.
    /// </summary>
    /// <remarks>
    /// version.json приходит из того же репозитория, но проверка хоста
    /// всё равно нужна: опечатка в манифесте иначе приводит к загрузке
    /// с постороннего сервера и непонятной ошибке, а подменённый
    /// манифест — к загрузке с произвольного адреса. Список совпадает
    /// с реально используемыми источниками: raw.githubusercontent,
    /// github.com/raw, зеркало jsDelivr и объекты GitHub Releases.
    /// </remarks>
    private static readonly HashSet<string> TrustedDownloadHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "raw.githubusercontent.com",
        "github.com",
        "cdn.jsdelivr.net",
        "objects.githubusercontent.com"
    };

    static UpdateService()
    {
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "MotionCommander-AutoUpdater");
        _downloadClient.DefaultRequestHeaders.Add("User-Agent", "MotionCommander-AutoUpdater");
    }

    public static string GetCurrentVersion()
    {
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        return ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "3.0.0";
    }

    public static string GetPortableZipUrl(string version)
    {
        string clean = (version ?? "").TrimStart('v', 'V');
        return $"https://raw.githubusercontent.com/BlackTecCom2000/MotionCommander/main/dist/MotionCommander-v{clean}-Portable.zip";
    }

    public static string GetPatchZipUrl(string version)
    {
        string clean = (version ?? "").TrimStart('v', 'V');
        return $"https://raw.githubusercontent.com/BlackTecCom2000/MotionCommander/main/dist/MotionCommander-v{clean}-Patch.zip";
    }

    public static async Task<UpdateInfo> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        string currentVerStr = GetCurrentVersion();
        var info = new UpdateInfo { CurrentVersion = currentVerStr, LatestVersion = currentVerStr };

        try
        {
            // 1. Манифест: raw.githubusercontent, затем зеркало jsDelivr
            //    (в части регионов raw.githubusercontent недоступен или
            //    медленный). Замеры 20.10.2026: оба адреса отдают
            //    version.json; основной путь — raw, зеркало — при сбое.
            string json = await FetchManifestJsonAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("version", out var vProp))
            {
                info.LatestVersion = vProp.GetString() ?? currentVerStr;
            }

            if (root.TryGetProperty("releaseDate", out var dProp))
            {
                info.ReleaseDate = dProp.GetString() ?? "";
            }

            if (root.TryGetProperty("patchUrl", out var patchProp))
            {
                info.PatchUrl = patchProp.GetString() ?? "";
            }

            if (root.TryGetProperty("patchSizeMb", out var patchMbProp) && patchMbProp.TryGetDouble(out var pMb))
            {
                info.PatchSizeMb = pMb;
            }

            if (root.TryGetProperty("patchMinVersion", out var minVerProp))
            {
                info.PatchMinVersion = minVerProp.GetString() ?? "3.8.0";
            }

            if (root.TryGetProperty("fullSizeMb", out var fullMbProp) && fullMbProp.TryGetDouble(out var fMb))
            {
                info.FullSizeMb = fMb;
            }

            if (root.TryGetProperty("downloadUrl", out var dlProp))
            {
                info.DownloadUrl = dlProp.GetString() ?? "";
            }

            if (root.TryGetProperty("installerUrl", out var instProp))
            {
                info.InstallerUrl = instProp.GetString() ?? "";
            }

            if (root.TryGetProperty("setupExeUrl", out var setupProp))
            {
                info.SetupExeUrl = setupProp.GetString() ?? "";
            }

            if (root.TryGetProperty("changelog", out var clProp) && clProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in clProp.EnumerateArray())
                {
                    string? line = item.GetString();
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        info.Changelog.Add(line);
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(info.PatchUrl) && !string.IsNullOrWhiteSpace(info.LatestVersion))
            {
                info.PatchUrl = $"https://raw.githubusercontent.com/BlackTecCom2000/MotionCommander/main/dist/MotionCommander-v{info.LatestVersion}-Patch.zip";
            }

            info.IsUpdateAvailable = IsNewerVersion(currentVerStr, info.LatestVersion);
            info.PackageType = (info.IsUpdateAvailable && info.CanApplyPatch(currentVerStr))
                ? UpdatePackageType.Patch
                : UpdatePackageType.FullPackage;

            UpdateLog.Info("check",
                $"Установлена v{currentVerStr}, доступна v{info.LatestVersion}; " +
                $"обновление={info.IsUpdateAvailable}; пакет={info.PackageType}; " +
                $"патч={info.PatchSizeMb:F1} МБ; полный={info.FullSizeMb:F1} МБ");

            return info;
        }
        catch (Exception ex)
        {
            UpdateLog.Warn("check", $"Манифест недоступен ({ex.Message}); пробую GitHub Releases API");
            // 2. Fallback: Запрос к официальному GitHub Releases API
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, GitHubReleasesUrl);
                using var resp = await _httpClient.SendAsync(request, ct);
                if (resp.IsSuccessStatusCode)
                {
                    string releaseJson = await resp.Content.ReadAsStringAsync(ct);
                    using var relDoc = JsonDocument.Parse(releaseJson);
                    var relRoot = relDoc.RootElement;
                    if (relRoot.TryGetProperty("tag_name", out var tagProp))
                    {
                        string tag = tagProp.GetString() ?? "";
                        string ver = tag.TrimStart('v', 'V');
                        info.LatestVersion = ver;
                        info.IsUpdateAvailable = IsNewerVersion(currentVerStr, ver);
                        if (relRoot.TryGetProperty("published_at", out var pubProp))
                        {
                            info.ReleaseDate = pubProp.GetString() ?? "";
                        }
                        if (relRoot.TryGetProperty("body", out var bodyProp))
                        {
                            string body = bodyProp.GetString() ?? "";
                            info.Changelog = body.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                        }
                        if (relRoot.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var a in assets.EnumerateArray())
                            {
                                if (a.TryGetProperty("browser_download_url", out var dlUrl))
                                {
                                    string dl = dlUrl.GetString() ?? "";
                                    if (dl.EndsWith("-Portable.zip", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(info.DownloadUrl))
                                    {
                                        info.DownloadUrl = dl;
                                        info.InstallerUrl = dl;
                                    }
                                    else if (dl.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(info.SetupExeUrl))
                                    {
                                        info.SetupExeUrl = dl;
                                    }
                                    else if (dl.EndsWith("-Patch.zip", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(info.PatchUrl))
                                    {
                                        info.PatchUrl = dl;
                                    }
                                }
                            }
                        }
                        if (string.IsNullOrEmpty(info.DownloadUrl))
                        {
                            info.DownloadUrl = $"https://raw.githubusercontent.com/BlackTecCom2000/MotionCommander/main/dist/MotionCommander-v{ver}-Portable.zip";
                            info.InstallerUrl = info.DownloadUrl;
                        }
                        if (string.IsNullOrEmpty(info.PatchUrl) && !string.IsNullOrWhiteSpace(info.LatestVersion))
                        {
                            info.PatchUrl = $"https://raw.githubusercontent.com/BlackTecCom2000/MotionCommander/main/dist/MotionCommander-v{info.LatestVersion}-Patch.zip";
                        }
                        if (string.IsNullOrEmpty(info.SetupExeUrl))
                        {
                            info.SetupExeUrl = $"https://raw.githubusercontent.com/BlackTecCom2000/MotionCommander/main/dist/MotionCommander-v{ver}-Setup.exe";
                        }
                        return info;
                    }
                }
            }
            catch { }

            info.ErrorMessage = ex.Message;
            UpdateLog.Error("check", $"Проверка обновлений невозможна: {ex.Message}");
            return info;
        }
    }

    public static int CompareVersions(string verA, string verB)
    {
        string cleanA = verA.TrimStart('v', 'V');
        string cleanB = verB.TrimStart('v', 'V');
        if (Version.TryParse(cleanA, out var vA) && Version.TryParse(cleanB, out var vB))
        {
            return vA.CompareTo(vB);
        }
        return string.Compare(cleanA, cleanB, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsNewerVersion(string currentStr, string targetStr)
    {
        return CompareVersions(targetStr, currentStr) > 0;
    }

    public static bool IsOlderVersion(string currentStr, string targetStr)
    {
        return CompareVersions(targetStr, currentStr) < 0;
    }

    public static async Task<List<ReleaseVersionItem>> GetAvailableReleasesAsync(CancellationToken ct = default)
    {
        string currentVer = GetCurrentVersion();
        var list = new List<ReleaseVersionItem>();
        var seenTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Попытка получить релизы через GitHub Releases API
        try
        {
            string releasesUrl = "https://api.github.com/repos/BlackTecCom2000/MotionCommander/releases?per_page=50";
            using var req = new HttpRequestMessage(HttpMethod.Get, releasesUrl);
            using var resp = await _httpClient.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                string json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var rel in doc.RootElement.EnumerateArray())
                    {
                        string tagName = rel.TryGetProperty("tag_name", out var tProp) ? (tProp.GetString() ?? "") : "";
                        if (string.IsNullOrWhiteSpace(tagName)) continue;

                        string verNum = tagName.TrimStart('v', 'V');
                        seenTags.Add(tagName);

                        var item = new ReleaseVersionItem
                        {
                            Version = verNum,
                            TagName = tagName,
                            Name = rel.TryGetProperty("name", out var nProp) ? (nProp.GetString() ?? tagName) : tagName,
                            ReleaseDate = rel.TryGetProperty("published_at", out var dProp) ? (dProp.GetString() ?? "") : "",
                            Body = rel.TryGetProperty("body", out var bProp) ? (bProp.GetString() ?? "") : ""
                        };

                        if (rel.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var a in assets.EnumerateArray())
                            {
                                if (a.TryGetProperty("browser_download_url", out var dlProp))
                                {
                                    string dl = dlProp.GetString() ?? "";
                                    if (dl.EndsWith("-Portable.zip", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(item.DownloadUrl))
                                    {
                                        item.DownloadUrl = dl;
                                    }
                                    else if (dl.EndsWith("-Patch.zip", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(item.PatchUrl))
                                    {
                                        item.PatchUrl = dl;
                                    }
                                    if (dl.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(item.SetupExeUrl))
                                    {
                                        item.SetupExeUrl = dl;
                                    }
                                }
                            }
                        }

                        if (string.IsNullOrEmpty(item.DownloadUrl))
                        {
                            item.DownloadUrl = $"https://raw.githubusercontent.com/BlackTecCom2000/MotionCommander/main/dist/MotionCommander-v{verNum}-Portable.zip";
                        }
                        if (string.IsNullOrEmpty(item.PatchUrl) && CompareVersions(verNum, "3.8.5") >= 0)
                        {
                            item.PatchUrl = $"https://raw.githubusercontent.com/BlackTecCom2000/MotionCommander/main/dist/MotionCommander-v{verNum}-Patch.zip";
                        }

                        // setupExeUrl НЕ достраивается по шаблону.
                        // Раньше здесь подставлялась ссылка вида
                        // ...-Setup.exe независимо от того, собрал ли
                        // инсталлятор релиз, и пользователю предлагалось
                        // скачать файл, которого в репозитории нет (404).
                        // Пустое значение означает «инсталлятора для этой
                        // версии нет», и интерфейс предлагает portable-архив.

                        int cmp = CompareVersions(verNum, currentVer);
                        item.IsCurrent = cmp == 0;
                        item.IsUpgrade = cmp > 0;
                        item.IsDowngrade = cmp < 0;

                        list.Add(item);
                    }
                }
            }
        }
        catch { }

        // 2. Fallback / Дополнение через GitHub Tags API
        try
        {
            string tagsUrl = "https://api.github.com/repos/BlackTecCom2000/MotionCommander/tags?per_page=50";
            using var req = new HttpRequestMessage(HttpMethod.Get, tagsUrl);
            using var resp = await _httpClient.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                string json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tag in doc.RootElement.EnumerateArray())
                    {
                        string tagName = tag.TryGetProperty("name", out var nProp) ? (nProp.GetString() ?? "") : "";
                        if (string.IsNullOrWhiteSpace(tagName) || seenTags.Contains(tagName)) continue;

                        string verNum = tagName.TrimStart('v', 'V');
                        seenTags.Add(tagName);

var item = new ReleaseVersionItem
                        {
                            Version = verNum,
                            TagName = tagName,
                            Name = $"Motion Commander {tagName}",
                            DownloadUrl = $"https://raw.githubusercontent.com/BlackTecCom2000/MotionCommander/main/dist/MotionCommander-v{verNum}-Portable.zip",
                            // Патч публикуется в dist начиная с 3.8.5.
                            PatchUrl = CompareVersions(verNum, "3.8.5") >= 0
                                ? $"https://raw.githubusercontent.com/BlackTecCom2000/MotionCommander/main/dist/MotionCommander-v{verNum}-Patch.zip"
                                : "",
                            // Инсталлятор появился в dist начиная с 3.8.2.
                            // Для более старых тегов ссылка не строится:
                            // иначе пользователь получал бы 404 на кнопке
                            // «скачать установщик».
                            // Пустое значение означает «инсталлятора для этой
                            // версии нет», и интерфейс предлагает portable-архив.
                            SetupExeUrl = CompareVersions(verNum, "3.8.2") >= 0
                                ? $"https://raw.githubusercontent.com/BlackTecCom2000/MotionCommander/main/dist/MotionCommander-v{verNum}-Setup.exe"
                                : "",
                            Body = $"Релиз Motion Commander {tagName}. Совместим со встроенным обновлением и откатом."
                        };

                        int cmp = CompareVersions(verNum, currentVer);
                        item.IsCurrent = cmp == 0;
                        item.IsUpgrade = cmp > 0;
                        item.IsDowngrade = cmp < 0;

                        list.Add(item);
                    }
                }
            }
        }
        catch { }

        // 3. Если сеть недоступна или нет тегов — fallback на список локально известных релизов
        if (list.Count == 0)
        {
            var fallbackVersions = new[]
            {
                "3.8.13", "3.8.12", "3.8.11", "3.8.10", "3.8.9", "3.8.7", "3.8.6", "3.8.5", "3.8.4", "3.8.3", "3.8.2", "3.8.1", "3.8.0", "3.7.1", "3.7.0", "3.0.0"
            };

            foreach (var v in fallbackVersions)
            {
                int cmp = CompareVersions(v, currentVer);

                // Состав каталога dist проверен по факту: Patch.zip
                // публикуется начиная с 3.8.5, Setup.exe — с 3.8.2,
                // Portable.zip — для всех перечисленных версий.
                // Ссылки на несуществующие файлы не строятся: оффлайн-
                // список не должен предлагать заведомый 404.
                bool hasPatch = CompareVersions(v, "3.8.5") >= 0;
                bool hasSetup = CompareVersions(v, "3.8.2") >= 0;

                list.Add(new ReleaseVersionItem
                {
                    Version = v,
                    TagName = $"v{v}",
                    Name = $"Motion Commander v{v}",
                    PatchUrl = hasPatch
                        ? $"https://raw.githubusercontent.com/BlackTecCom2000/MotionCommander/main/dist/MotionCommander-v{v}-Patch.zip"
                        : "",
                    DownloadUrl = $"https://raw.githubusercontent.com/BlackTecCom2000/MotionCommander/main/dist/MotionCommander-v{v}-Portable.zip",
                    SetupExeUrl = hasSetup
                        ? $"https://raw.githubusercontent.com/BlackTecCom2000/MotionCommander/main/dist/MotionCommander-v{v}-Setup.exe"
                        : "",
                    Body = $"Релиз v{v} из дистрибутивов Motion Commander.",
                    IsCurrent = cmp == 0,
                    IsUpgrade = cmp > 0,
                    IsDowngrade = cmp < 0
                });
            }
        }

        // Сортировка от новейших к старейшим
        list.Sort((a, b) => CompareVersions(b.Version, a.Version));

        return list;
    }

    /// <summary>
    /// Скачивает пакет обновления с повторами, докачкой с места обрыва и
    /// зеркалами. Контрольная сумма сверяется до распаковки и запуска.
    /// </summary>
    /// <remarks>
    /// <para>Поведение проверено реальными запросами 20.10.2026:
    /// raw.githubusercontent и cdn.jsdelivr.net поддерживают Range
    /// (HTTP 206), поэтому прерванная загрузка продолжается с места
    /// обрыва, а не начинается с нуля.</para>
    ///
    /// <para>Зеркало jsDelivr отдаёт файлы до 20 МБ: Portable.zip (38 МБ)
    /// получает 404, Setup.exe (21.7 МБ) — 403. Поэтому зеркало
    /// добавляется только для файлов, которые оно реально обслуживает.</para>
    /// </remarks>
    public static async Task<string> DownloadUpdateAsync(
        string downloadUrl, 
        IProgress<(long bytesRead, long totalBytes, int percent, double speedMBps)>? progress = null, 
        CancellationToken ct = default,
        long expectedBytes = 0)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl))
            throw new ArgumentException("Ссылка на загрузку обновления не указана.");

        string tempFolder = Path.Combine(Path.GetTempPath(), "MotionCommander-Update");
        Directory.CreateDirectory(tempFolder);
        CleanStaleTempFiles(tempFolder);

        // Место проверяется ДО первого байта загрузки. Иначе диск
        // заполнялся бы на середине архива, загрузка падала бы с
        // невнятным IOException, а пользователь искал бы причину в сети.
        // Запас сверх размера файла нужен на служебные записи NTFS и на
        // уже существующий .part, с которого загрузка продолжится.
        EnsureFreeSpace(
            tempFolder,
            expectedBytes > 0 ? expectedBytes + FreeSpaceReserveBytes : 0,
            "загрузки обновления");

        var sourceUri = new Uri(downloadUrl);
        ValidateDownloadHost(sourceUri);

        string originalFileName = Path.GetFileName(sourceUri.LocalPath);
        if (string.IsNullOrEmpty(originalFileName)) originalFileName = "MotionCommander-Update.zip";
        string targetFilePath = Path.Combine(tempFolder, originalFileName);

        // Уникальный временный файл (.part) изолирует процесс загрузки от внешних блокировок и остаточных файлов.
        // ВАЖНО: нельзя писать {Guid.NewGuid():N[..8]} — «N[..8]» уходит в Guid.ToString(format)
        // и всегда бросает FormatException("Format string can be only ...").
        // Берём первые 8 символов N-формы после форматирования.
        string uniquePartPath = Path.Combine(tempFolder, $"{originalFileName}.{Guid.NewGuid().ToString("N")[..8]}.part");

        var mirrors = GetMirrorCandidates(downloadUrl);
        UpdateLog.Info("download", $"Файл {originalFileName}; адресов-кандидатов: {mirrors.Count}");

        Exception? lastError = null;
        Exception? lastIntegrityError = null;
        var totalStopwatch = Stopwatch.StartNew();

        foreach (string candidate in mirrors)
        {
            string host;
            try { host = new Uri(candidate).Host; } catch { host = candidate; }

            // Для файлов на плавающей ссылке зеркала кэш сбрасывается один
            // раз перед попытками; для теговых ссылок это ничего не делает.
            await TryPurgeCdnCacheAsync(candidate, ct);

            for (int attempt = 1; attempt <= DownloadAttemptsPerMirror; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var attemptStopwatch = Stopwatch.StartNew();
                try
                {
                    await DownloadFileOnceAsync(candidate, uniquePartPath, progress, ct);

                    // Потоки записи закрыты и освобождены.
                    // Целостность проверяется ДО распаковки и запуска.
                    string calculatedSha256 = await ComputeFileSha256WithRetryAsync(uniquePartPath, ct);
                    await VerifyIntegrityAsync(uniquePartPath, originalFileName, calculatedSha256, ct);

                    // Безопасное повышение временного файла до целевого пути с повторными попытками.
                    // Если целевой файл заблокирован другим процессом (антивирусом или Проводником),
                    // возвращаем проверенный путь .part напрямую, чтобы не прерывать обновление.
                    string finalPath = await SafePromoteDownloadedFileAsync(uniquePartPath, targetFilePath, ct);

                    UpdateLog.Info("download",
                        $"Загрузка завершена за {totalStopwatch.Elapsed.TotalSeconds:F1} с " +
                        $"({host}, попытка {attempt}); SHA-256 подтверждён");
                    return finalPath;
                }
                catch (OperationCanceledException)
                {
                    UpdateLog.Warn("download", "Загрузка отменена пользователем");
                    throw;
                }
                catch (UpdateIntegrityException ex)
                {
                    // Содержимое не совпало с манифестом. Повторять с этого же
                    // зеркала бессмысленно; пробуем следующее, но если и там
                    // расхождение — пользователь увидит именно причину.
                    lastIntegrityError = ex;
                    UpdateLog.Error("download", $"Проверка SHA-256 не прошла ({host}): {ex.Message}");
                    TryDelete(uniquePartPath);
                    break;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                {
                    lastError = ex;
                    UpdateLog.Warn("download",
                        $"Попытка {attempt} из {DownloadAttemptsPerMirror} ({host}) " +
                        $"провалилась за {attemptStopwatch.Elapsed.TotalSeconds:F1} с: {ex.Message}");

                    if (attempt < DownloadAttemptsPerMirror)
                        await Task.Delay(500 * attempt, ct);
                }
            }
        }

        TryDelete(uniquePartPath);

        if (lastIntegrityError != null)
            throw lastIntegrityError;

        throw new IOException(
            $"Не удалось скачать обновление ни с одного из {mirrors.Count} адресов " +
            $"(попыток на адрес: {DownloadAttemptsPerMirror}). " +
            $"Последняя ошибка: {lastError?.Message ?? "неизвестна"}",
            lastError);
    }

    public static bool IsTrustedDownloadUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.IsLoopback) return true;
        return uri.Scheme == Uri.UriSchemeHttps && TrustedDownloadHosts.Contains(uri.Host);
    }

    /// <summary>Запас сверх размера файла: служебные записи тома и .part.</summary>
    private const long FreeSpaceReserveBytes = 64L * 1024 * 1024;

    /// <summary>Свободно байт на томе, где лежит каталог; -1 — определить не удалось.</summary>
    public static long GetAvailableFreeBytes(string directory)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(directory));
            if (string.IsNullOrEmpty(root)) return -1;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            // Сетевой путь, пустой том или нет прав: проверка места
            // невозможна. Это не повод отменять обновление — просто
            // решение принимается без неё, о чём пишется в журнал.
            return -1;
        }
    }

    /// <summary>
    /// Проверяет, что на томе достаточно места для операции.
    /// </summary>
    /// <remarks>
    /// Сообщение содержит оба числа — сколько нужно и сколько есть: по
    /// «недостаточно места» пользователь не может решить, что удалять.
    /// </remarks>
    public static void EnsureFreeSpace(string directory, long requiredBytes, string purpose)
    {
        if (requiredBytes <= 0) return;

        long free = GetAvailableFreeBytes(directory);
        if (free < 0)
        {
            UpdateLog.Warn("space", $"Свободное место для {purpose} определить не удалось ({directory})");
            return;
        }

        if (free >= requiredBytes)
        {
            UpdateLog.Info("space",
                $"{purpose}: нужно {FormatMegabytes(requiredBytes)}, свободно {FormatMegabytes(free)} ({directory})");
            return;
        }

        long missing = requiredBytes - free;
        UpdateLog.Error("space",
            $"{purpose}: нужно {FormatMegabytes(requiredBytes)}, свободно {FormatMegabytes(free)} " +
            $"(не хватает {FormatMegabytes(missing)}, {directory})");

        throw new IOException(
            $"Недостаточно места на диске для {purpose}.{Environment.NewLine}{Environment.NewLine}" +
            $"Нужно:    {FormatMegabytes(requiredBytes)}{Environment.NewLine}" +
            $"Свободно: {FormatMegabytes(free)}{Environment.NewLine}" +
            $"Не хватает {FormatMegabytes(missing)}.{Environment.NewLine}{Environment.NewLine}" +
            "Освободите место и повторите. Файлы программы не изменены.");
    }

    private static string FormatMegabytes(long bytes) =>
        $"{bytes / (1024.0 * 1024.0):F1} МБ";

    /// <summary>
    /// Проверяет, что пакет скачивается по HTTPS с адреса проекта.
    /// </summary>
    /// <remarks>
    /// Исключение для loopback нужно локальным тестам и оффлайн-зеркалам:
    /// это не сетевой адрес, и подменить им официальный источник нельзя.
    /// </remarks>
    private static void ValidateDownloadHost(Uri uri)
    {
        if (uri.IsLoopback) return;

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new IOException(
                $"Обновление можно скачать только по HTTPS, а ссылка использует «{uri.Scheme}». " +
                "Загрузка отменена.");
        }

        if (!TrustedDownloadHosts.Contains(uri.Host))
        {
            throw new IOException(
                $"Ссылка на обновление указывает на неизвестный сервер «{uri.Host}». " +
                "Загрузка отменена: пакет должен приходить только с официальных " +
                "адресов проекта.");
        }

        UpdateLog.Info("download", $"Источник обновления: {uri.Host}{uri.AbsolutePath}");
    }

    /// <summary>
    /// Сбрасывает кэш jsDelivr для файла, если адрес ссылается на
    /// плавающую ветку (@latest/@main).
    /// </summary>
    /// <remarks>
    /// <para>Измерено 20.10.2026: после публикации 3.8.53 ссылка
    /// <c>@latest/version.json</c> продолжает отдавать 3.8.52, хотя
    /// <c>@v3.8.53/version.json</c> уже свежий. После вызова
    /// <c>purge.jsdelivr.net</c> та же ссылка отдаёт 3.8.53. Без сброса
    /// зеркало в регионах с недоступным raw.githubusercontent показывало
    /// бы «обновлений нет».</para>
    ///
    /// <para>Сброс — best-effort: он ограничен 5 секундами и никогда не
    /// срывает обновление. Файлы, привязанные к тегу версии, не сбрасываются:
    /// их содержимое неизменяемо.</para>
    /// </remarks>
    public static async Task TryPurgeCdnCacheAsync(string url, CancellationToken ct)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
            if (!uri.Host.Equals("cdn.jsdelivr.net", StringComparison.OrdinalIgnoreCase)) return;
            if (!uri.AbsolutePath.Contains("@latest", StringComparison.OrdinalIgnoreCase) &&
                !uri.AbsolutePath.Contains("@main", StringComparison.OrdinalIgnoreCase)) return;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            string purgeUrl = "https://purge.jsdelivr.net" + uri.AbsolutePath;
            using var response = await _httpClient.GetAsync(purgeUrl, timeout.Token);

            UpdateLog.Info("manifest",
                $"jsDelivr purge: HTTP {(int)response.StatusCode} ({uri.AbsolutePath})");
        }
        catch (Exception ex)
        {
            UpdateLog.Warn("manifest", $"jsDelivr purge не удался: {ex.Message}");
        }
    }

    /// <summary>Скачивает файл за один заход, продолжая .part через HTTP Range.</summary>
    private static async Task DownloadFileOnceAsync(
        string url,
        string partPath,
        IProgress<(long bytesRead, long totalBytes, int percent, double speedMBps)>? progress,
        CancellationToken ct)
    {
        long existingBytes = 0;
        try
        {
            if (File.Exists(partPath)) existingBytes = new FileInfo(partPath).Length;
        }
        catch
        {
            existingBytes = 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existingBytes > 0)
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existingBytes, null);

        using var response = await _downloadClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // .part длиннее файла на сервере (например, файл перезалили):
            // начинаем заново, следующий заход скачает всё.
            TryDelete(partPath);
            throw new IOException("Сервер отклонил продолжение загрузки (HTTP 416); файл будет скачан заново.");
        }

        response.EnsureSuccessStatusCode();

        bool resumed = response.StatusCode == System.Net.HttpStatusCode.PartialContent && existingBytes > 0;
        if (!resumed) existingBytes = 0; // сервер отдал полный файл — пишем с нуля

        long totalBytes = -1;
        if (resumed && response.Content.Headers.ContentRange?.Length is long rangeTotal)
            totalBytes = rangeTotal;
        else if (response.Content.Headers.ContentLength is long contentLength)
            totalBytes = contentLength;

        if (resumed)
            UpdateLog.Info("download", $"Продолжение загрузки с {existingBytes} байт");

        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(
            partPath,
            resumed ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            true);

        byte[] buffer = new byte[81920];
        long received = existingBytes;
        var sessionWatch = Stopwatch.StartNew();

        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        while (true)
        {
            // Сторож простоя: 60 секунд без единого байта — это мёртвое
            // соединение, а не медленный канал. Общего лимита на файл нет.
            idleCts.CancelAfter(TimeSpan.FromSeconds(IdleTimeoutSeconds));

            int bytesRead;
            try
            {
                bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, idleCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new IOException(
                    $"Соединение не передаёт данные более {IdleTimeoutSeconds} секунд; " +
                    "следующая попытка продолжит загрузку с того же места.");
            }

            if (bytesRead <= 0) break;

            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            received += bytesRead;

            if (progress != null)
            {
                double seconds = sessionWatch.Elapsed.TotalSeconds;
                double sessionBytes = received - existingBytes;
                double speed = seconds > 0 ? (sessionBytes / (1024.0 * 1024.0)) / seconds : 0;
                int percent = totalBytes > 0
                    ? (int)Math.Min(100, received * 100.0 / totalBytes)
                    : 0;
                progress.Report((received, totalBytes, percent, speed));
            }
        }

        await fileStream.FlushAsync(ct);

        if (totalBytes > 0 && received < totalBytes)
            throw new IOException(
                $"Соединение закрылось до конца файла: получено {received} из {totalBytes} байт. " +
                "Следующая попытка продолжит загрузку с того же места.");
    }

    /// <summary>
    /// Список адресов для загрузки: основной, затем зеркала, которые
    /// реально отдают этот файл.
    /// </summary>
    public static IReadOnlyList<string> GetMirrorCandidates(string url)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(url)) return list;

        list.Add(url);

        if (!url.StartsWith(RawBaseUrl, StringComparison.OrdinalIgnoreCase))
            return list;

        string relative = url[RawBaseUrl.Length..];

        // Замерено 20.10.2026: jsDelivr отдаёт файлы до 20 МБ.
        // Portable.zip (38 МБ) -> HTTP 404, Setup.exe (21.7 МБ) -> HTTP 403.
        bool tooLargeForJsDelivr =
            relative.EndsWith("-Portable.zip", StringComparison.OrdinalIgnoreCase) ||
            relative.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase);

        if (!tooLargeForJsDelivr)
        {
            // Файл привязан к тегу своей версии: ссылка на тег неизменяема,
            // поэтому зеркало не может отдать чужой файл из будущего релиза.
            // Для имени без версии остаётся @latest (см. JsDelivrLatestBase).
            string? fileVersion = TryExtractVersionFromFileName(Path.GetFileName(relative));
            string reference = !string.IsNullOrEmpty(fileVersion) ? $"@v{fileVersion}" : "@latest";
            list.Add($"{JsDelivrRepo}{reference}/{relative}");
        }

        // Тот же файл через github.com/raw: другой хост, другая блокировка,
        // тот же коммит main.
        list.Add($"{GitHubRepoUrl}/raw/main/{relative}");

        return list;
    }

    /// <summary>Адреса манифеста version.json по порядку.</summary>
    public static IReadOnlyList<string> GetManifestCandidates() => new[]
    {
        ManifestUrl,
        $"{JsDelivrLatestBase}version.json"
    };

    /// <summary>Читает version.json с основного адреса или зеркала, с журналом попыток.</summary>
    private static async Task<string> FetchManifestJsonAsync(CancellationToken ct)
    {
        Exception? lastError = null;
        foreach (string candidate in GetManifestCandidates())
        {
            var sw = Stopwatch.StartNew();
            try
            {
                string urlWithBypass = $"{candidate}?cache_bypass={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

                // Плавающая ссылка на зеркале кэшируется: без сброса после
                // релиза она отдаёт прошлый манифест (измерено).
                await TryPurgeCdnCacheAsync(candidate, ct);

                string json = await _httpClient.GetStringAsync(urlWithBypass, ct);
                UpdateLog.Info("manifest",
                    $"{new Uri(candidate).Host}: {json.Length} байт за {sw.Elapsed.TotalMilliseconds:F0} мс");
                return json;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                lastError = ex;
                UpdateLog.Warn("manifest",
                    $"{new Uri(candidate).Host}: сбой за {sw.Elapsed.TotalMilliseconds:F0} мс — {ex.Message}");
            }
        }

        throw lastError ?? new IOException("Ни один адрес манифеста не ответил.");
    }

    /// <summary>Удаляет старые .part и архивы прошлых обновлений (старше 7 дней).</summary>
    private static void CleanStaleTempFiles(string tempFolder)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-7);
            foreach (string file in Directory.EnumerateFiles(tempFolder))
            {
                try
                {
                    var info = new FileInfo(file);
                    bool ours = file.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
                                Path.GetFileName(file).StartsWith("MotionCommander-", StringComparison.OrdinalIgnoreCase);
                    if (ours && info.LastWriteTimeUtc < cutoff)
                        info.Delete();
                }
                catch
                {
                    // Файл занят другим процессом — пропускаем, это не ошибка обновления.
                }
            }
        }
        catch
        {
            // Каталог недоступен — чистка не является условием обновления.
        }
    }

    /// <summary>Версия из имени файла вида MotionCommander-v3.8.52-Patch.zip.</summary>
    internal static string? TryExtractVersionFromFileName(string fileName)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            fileName ?? string.Empty,
            @"MotionCommander-v([0-9]+(?:\.[0-9]+){1,3})-",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Проверка автообновления не чаще заданного интервала.</summary>
    public static bool IsAutoCheckDue(TimeSpan interval)
    {
        try
        {
            string path = Path.Combine(AppPaths.WritableDataDirectory, "update_check.json");
            if (!File.Exists(path)) return true;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("lastCheckUtc", out var lastProp)) return true;
            if (!DateTime.TryParse(lastProp.GetString(), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var last)) return true;

            return DateTime.UtcNow - last >= interval;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Запоминает время успешной проверки обновлений.</summary>
    public static void RecordAutoCheck(string latestVersion)
    {
        try
        {
            string path = Path.Combine(AppPaths.WritableDataDirectory, "update_check.json");
            Directory.CreateDirectory(AppPaths.WritableDataDirectory);
            var payload = new Dictionary<string, string>
            {
                ["lastCheckUtc"] = DateTime.UtcNow.ToString("O"),
                ["latestVersion"] = latestVersion ?? ""
            };
            File.WriteAllText(path, JsonSerializer.Serialize(payload));
        }
        catch
        {
            // Не критично для обновления.
        }
    }

    private static async Task<string> SafePromoteDownloadedFileAsync(string partPath, string targetPath, CancellationToken ct)
    {
        const int maxAttempts = 5;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                }
                File.Move(partPath, targetPath, overwrite: true);
                return targetPath;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == maxAttempts)
                {
                    // Целевой файл заблокирован внешним процессом. Возвращаем проверенный partPath,
                    // так как ZipFile и UpdateEngine могут читать из любого валидного пути к файлу.
                    return partPath;
                }
                await Task.Delay(250 * attempt, ct);
            }
        }
        return partPath;
    }

    /// <summary>
    /// Сверяет контрольную сумму скачанного файла с манифестом релизов.
    /// </summary>
    private static async Task VerifyIntegrityAsync(
        string filePath, 
        string originalFileName, 
        string? precalculatedHash, 
        CancellationToken ct)
    {
        // Версия из имени файла (MotionCommander-v3.8.52-Patch.zip) позволяет
        // отличить «файл повреждён» от «манифест устарел»: устаревший
        // checksums.json с CDN иначе выглядел бы как подмена содержимого.
        string? expectedVersion = TryExtractVersionFromFileName(originalFileName);
        string expected = await ReleaseManifest.GetSha256ForAsync(originalFileName, ct, expectedVersion);

        if (string.IsNullOrEmpty(expected))
        {
            TryDelete(filePath);
            throw new UpdateIntegrityException(
                $"Манифест релизов не содержит контрольной суммы для файла {originalFileName}. " +
                "Обновление прервано: неподтверждённое содержимое нельзя " +
                "запускать с правами администратора.");
        }

        string actual = !string.IsNullOrEmpty(precalculatedHash)
            ? precalculatedHash
            : await ComputeFileSha256WithRetryAsync(filePath, ct);

        if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) return;

        TryDelete(filePath);
        throw new UpdateIntegrityException(
            $"Контрольная сумма не совпала.{Environment.NewLine}{Environment.NewLine}" +
            $"Ожидалась: {expected}{Environment.NewLine}" +
            $"Получено:  {actual}{Environment.NewLine}{Environment.NewLine}" +
            "Файл удалён, обновление не применяется. Такое расхождение бывает " +
            "при повреждении загрузки или при подмене содержимого. " +
            "Обновляйтесь с официального адреса проекта.");
    }

    private static async Task<string> ComputeFileSha256WithRetryAsync(string filePath, CancellationToken ct)
    {
        const int maxAttempts = 5;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, true);
                using var sha = System.Security.Cryptography.SHA256.Create();
                return Convert.ToHexString(await sha.ComputeHashAsync(stream, ct));
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                await Task.Delay(200 * attempt, ct);
            }
        }

        using var finalStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, true);
        using var finalSha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(await finalSha.ComputeHashAsync(finalStream, ct));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* файл недоступен для удаления — это не повод пропустить проверку */ }
    }

    private static void SafeDeleteDirectory(string dirPath, int maxAttempts = 5)
    {
        for (int i = 1; i <= maxAttempts; i++)
        {
            try
            {
                if (Directory.Exists(dirPath))
                {
                    Directory.Delete(dirPath, true);
                }
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (i == maxAttempts) throw;
                Thread.Sleep(200 * i);
            }
        }
    }

    /// <summary>
    /// Распаковывает обновление во ВРЕМЕННЫЙ КАТАЛОГ ДАННЫХ, а не рядом с
    /// программой.
    /// </summary>
    public static async Task<string> PrepareStagingAsync(
        string zipPath,
        CancellationToken ct = default,
        string? expectedTargetVersion = null)
    {
        string stagingDir = AppPaths.StagingDirectory;
        UpdateLog.Info("staging", $"Распаковка {Path.GetFileName(zipPath)} в {stagingDir}");

        // Архив и его распакованное содержимое занимают место одновременно:
        // каталог подготовки освобождается только после успешного применения.
        // Двойной размер — измеренная оценка для сборок Motion Commander
        // (dll и json не сжимаются заметно), плюс запас на служебные записи.
        long zipBytes = 0;
        try { zipBytes = new FileInfo(zipPath).Length; }
        catch { zipBytes = 0; }

        EnsureFreeSpace(
            stagingDir,
            zipBytes > 0 ? zipBytes * 2 + FreeSpaceReserveBytes : 0,
            "распаковки обновления");

        try
        {
            if (Directory.Exists(stagingDir))
            {
                // Каталог могли занять символической ссылкой или junction.
                // Проверяем ReparsePoint, иначе удаление ушло бы наружу.
                var info = new DirectoryInfo(stagingDir);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException(
                        $"Каталог подготовки {stagingDir} является ссылкой. " +
                        "Укажите другой каталог данных и повторите обновление.");
                }
                SafeDeleteDirectory(stagingDir);
            }

            Directory.CreateDirectory(stagingDir);
            await Task.Run(() =>
            {
                const int maxAttempts = 3;
                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    try
                    {
                        System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, stagingDir, true);
                        return;
                    }
                    catch (IOException) when (attempt < maxAttempts)
                    {
                        Thread.Sleep(300 * attempt);
                    }
                }
            }, ct).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException(
                $"Нет доступа к каталогу подготовки обновления: {stagingDir}. " +
                "Проверьте права на папку данных приложения.", ex);
        }

        // Пустая распаковка — это не обновление, а повреждённый архив.
        // Без этой проверки скрипт обновления стёр бы каталог программы
        // и запустил её без единого обновлённого файла.
        int staged = Directory.GetFiles(stagingDir, "*", SearchOption.AllDirectories).Length;
        if (staged == 0)
            throw new IOException("Архив обновления распакован, но не содержит ни одного файла.");

        // Проверка совместимости патча с установленной версией программы.
        // Если внутри архива присутствует манифест патча (patch_manifest.json),
        // проверяем минимальную совместимую версию.
        string manifestFile = Path.Combine(stagingDir, "patch_manifest.json");
        if (File.Exists(manifestFile))
        {
            try
            {
                string manifestJson = File.ReadAllText(manifestFile);
                using var mDoc = JsonDocument.Parse(manifestJson);
                var mRoot = mDoc.RootElement;
                string minVer = mRoot.TryGetProperty("minCompatibleVersion", out var mv) ? (mv.GetString() ?? "") : "";
                string targetVer = mRoot.TryGetProperty("targetVersion", out var tv) ? (tv.GetString() ?? "") : "";

                string currentVer = GetCurrentVersion();
                if (!string.IsNullOrWhiteSpace(minVer) && CompareVersions(currentVer, minVer) < 0)
                {
                    throw new PatchIncompatibleException(
                        $"Данный патч предназначен для версий не ниже v{minVer}, а установлена v{currentVer}. " +
                        "Патч не подходит к текущей версии; требуется загрузка полного установочного пакета.",
                        currentVer, targetVer, minVer);
                }

                // Патч обязан вести именно в ту версию, которую показал
                // манифест. Без этой проверки устаревший или чужой патч
                // мог молча «откатить» приложение в другую версию.
                if (!string.IsNullOrWhiteSpace(expectedTargetVersion) &&
                    !string.IsNullOrWhiteSpace(targetVer) &&
                    CompareVersions(targetVer, expectedTargetVersion) != 0)
                {
                    throw new PatchIncompatibleException(
                        $"Патч ведёт в версию v{targetVer}, а ожидается v{expectedTargetVersion}. " +
                        "Скачался патч от другой версии; требуется полный установочный пакет.",
                        currentVer, targetVer, minVer);
                }

                UpdateLog.Info("staging",
                    $"Манифест патча: minCompatibleVersion={minVer}, targetVersion={targetVer}, установлена v{currentVer}");
            }
            catch (PatchIncompatibleException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new PatchIncompatibleException(
                    $"Манифест совместимости патча повреждён: {ex.Message}. Рекомендуется загрузить полный установочный пакет.",
                    inner: ex);
            }
        }

        // Целевая версия и состав пакета проверяются до применения:
        // см. ValidateStagedPackage.
        ValidateStagedPackage(stagingDir, expectedTargetVersion);

        return stagingDir;
    }

    public static bool IsInstalledVersion()
    {
        try
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            if (File.Exists(Path.Combine(baseDir, "unins000.exe")))
                return true;

            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{D37D5726-2F1E-4B07-B25C-2150E697DF2A}_is1");
            if (key != null)
                return true;
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Удаляет старые резервные копии, оставляя только указанное количество свежих.
    /// </summary>
    private static void PruneOldBackups(string backupRoot, int maxToKeep = 3)
    {
        try
        {
            if (!Directory.Exists(backupRoot)) return;
            var dirInfo = new DirectoryInfo(backupRoot);
            var subdirs = dirInfo.GetDirectories("backup_v*")
                .OrderByDescending(d => d.CreationTimeUtc)
                .ToList();

            if (subdirs.Count > maxToKeep)
            {
                foreach (var oldDir in subdirs.Skip(maxToKeep))
                {
                    try { oldDir.Delete(true); } catch { }
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// Готовит и запускает применение обновления после выхода программы с сохранением
    /// резервной копии и гарантией автоматического отката (rollback) в случае ошибок.
    /// </summary>
    /// <exception cref="UACDeclinedException">Пользователь отказал в повышении прав.</exception>
    public static void ApplySeamlessUpdate(string stagingFolder, AppState currentState)
    {
        string currentDir = AppDomain.CurrentDomain.BaseDirectory;

        // Путь исполняемого файла берётся у ЖИВОГО процесса, а не
        // склеивается из имени: иначе после переименования или запуска из
        // другой папки скрипт запустил бы несуществующий файл.
        string? runningExe = Environment.ProcessPath;
        string exePath = !string.IsNullOrEmpty(runningExe) && File.Exists(runningExe)
            ? runningExe
            : Path.Combine(currentDir, "Win11CopyDialog.exe");

        // Состояние сохраняется в каталог ДАННЫХ. Раньше оно писалось в
        // BaseDirectory, и при установке в Program Files без прав
        // администратора File.WriteAllText падал с «Отказано в доступе»
        // ДО запуска скрипта, то есть обновление не применялось вовсе.
        string stateFilePath = AppPaths.AppStateFile;
        try
        {
            string? stateDir = Path.GetDirectoryName(stateFilePath);
            if (!string.IsNullOrEmpty(stateDir)) Directory.CreateDirectory(stateDir);
            File.WriteAllText(stateFilePath, JsonSerializer.Serialize(currentState));
        }
        catch (Exception ex)
        {
            throw new IOException($"Не удалось сохранить состояние окна для передачи в обновлённую версию: {ex.Message}", ex);
        }

        var files = Directory.GetFiles(stagingFolder, "*", SearchOption.AllDirectories);
        if (files.Length == 0)
            throw new IOException("В подготовленном обновлении нет ни одного файла — применяться нечего.");

        // Повторная проверка состава непосредственно перед заменой файлов:
        // staging мог быть изменён или очищен после PrepareStagingAsync.
        ValidateStagedPackage(stagingFolder, expectedTargetVersion: null);

        // Подготовка каталога резервной копии перед заменой файлов
        string backupRoot = AppPaths.BackupDirectory;
        PruneOldBackups(backupRoot, maxToKeep: 3);

        string currentVer = GetCurrentVersion();
        string targetVersion = TryReadTargetVersion(stagingFolder);
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string backupFolder = Path.Combine(backupRoot, $"backup_v{currentVer}_{timestamp}");

        // Нужно ли повышение привилегий: проверяется реальной записью
        // рядом с программой, а не предположением о пути установки.
        bool needsElevation = !IsDirectoryWritable(currentDir);

        // Маркер успешного применения: обновлённая версия при следующем
        // запуске сообщит «обновление применено» и удалит маркер.
        string markerPath = Path.Combine(AppPaths.WritableDataDirectory, "update_applied.txt");
        // Парный маркер отказа: если скрипт откатился, пользователь узнает
        // об этом при следующем запуске, а не по «версия не изменилась».
        string failedMarkerPath = Path.Combine(AppPaths.WritableDataDirectory, "update_failed.txt");
        string updateLogPath = UpdateLog.LogFilePath;

        UpdateLog.Info("apply",
            $"Применение v{currentVer} → v{targetVersion}: файлов {files.Length}, " +
            $"резервная копия {backupFolder}, повышение прав: {(needsElevation ? "да" : "нет")}");

        // Скрипт запускается ПОСЛЕ выхода процесса: так снимаются все
        // блокировки на собственные исполняемые файлы. Лежит в каталоге
        // данных, а не в staging: staging удаляется целиком.
        string batPath = Path.Combine(AppPaths.WritableDataDirectory, "mc_update.bat");
        int currentPid = Environment.ProcessId;

        // Внутри кавычек cmd всё равно раскрывает %VAR%; профиль
        // пользователя может содержать %, поэтому каждый путь
        // экранируется удвоением (см. EscapeBatchPath). Без этого
        // скрипт молча ломался.

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("chcp 65001 >nul");
        sb.AppendLine("echo ========================================================");
        sb.AppendLine("echo   Motion Commander — Seamless Update Engine");
        sb.AppendLine("echo ========================================================");
        sb.AppendLine($"echo [%DATE% %TIME%] [bat] Update started v{currentVer} -^> v{targetVersion} >> \"{EscapeBatchPath(updateLogPath)}\"");
        sb.AppendLine("echo Waiting for Motion Commander to close...");

        // Проверка живого процесса по CSV-выводу tasklist.
        // Ожидаем до 5 секунд, после чего принудительно завершаем зависший процесс,
        // чтобы блокировка исполняемых файлов не сорвала обновление.
        sb.AppendLine("set wait_count=0");
        sb.AppendLine(":waitloop");
        sb.AppendLine($"tasklist /FI \"PID eq {currentPid}\" /NH /FO CSV 2>NUL | findstr /C:\"{currentPid}\" >NUL");
        sb.AppendLine("if \"%ERRORLEVEL%\"==\"0\" (");
        sb.AppendLine("    set /a wait_count+=1");
        sb.AppendLine($"    if %wait_count% geq 5 (taskkill /F /PID {currentPid} >nul 2>&1)");
        sb.AppendLine("    timeout /t 1 /nobreak >nul");
        sb.AppendLine("    goto waitloop");
        sb.AppendLine(")");
        sb.AppendLine("timeout /t 1 /nobreak >nul");
        sb.AppendLine("echo Application closed. Initializing update...");
        sb.AppendLine("taskkill /F /IM motion.exe >nul 2>&1");
        sb.AppendLine("taskkill /F /IM MotionCommanderDiagnostics.exe >nul 2>&1");

        // Шаг 1: Создание резервной копии файлов перед заменой (для отката)
        sb.AppendLine("echo [1/3] Creating rollback backup of existing files...");
        sb.AppendLine($"if not exist \"{EscapeBatchPath(backupFolder)}\" mkdir \"{EscapeBatchPath(backupFolder)}\"");
        sb.AppendLine($"echo Backup v{currentVer} created on %DATE% %TIME% > \"{EscapeBatchPath(Path.Combine(backupFolder, "backup_info.txt"))}\"");

        foreach (var file in files)
        {
            string relative = file[(stagingFolder.Length + 1)..];
            string dest = Path.Combine(currentDir, relative);
            string backupDest = Path.Combine(backupFolder, relative);
            string backupDestDir = Path.GetDirectoryName(backupDest) ?? backupFolder;

            sb.AppendLine($"if exist \"{EscapeBatchPath(dest)}\" (");
            sb.AppendLine($"    if not exist \"{EscapeBatchPath(backupDestDir)}\" mkdir \"{EscapeBatchPath(backupDestDir)}\"");
            sb.AppendLine($"    copy /Y \"{EscapeBatchPath(dest)}\" \"{EscapeBatchPath(backupDest)}\" >nul");
            sb.AppendLine(")");
        }

        // Шаг 2: Копирование обновлённых файлов поверх текущих файлов программы
        sb.AppendLine("echo [2/3] Copying update files in-place...");
        foreach (var file in files)
        {
            string relative = file[(stagingFolder.Length + 1)..];
            string dest = Path.Combine(currentDir, relative);
            string destDir = Path.GetDirectoryName(dest) ?? currentDir;
            sb.AppendLine($"if not exist \"{EscapeBatchPath(destDir)}\" mkdir \"{EscapeBatchPath(destDir)}\"");
            sb.AppendLine($"call :safe_copy \"{EscapeBatchPath(file)}\" \"{EscapeBatchPath(dest)}\"");
            sb.AppendLine("if errorlevel 1 (");
            sb.AppendLine($"    echo [%DATE% %TIME%] [bat] ERROR: failed to replace {relative} >> \"{EscapeBatchPath(updateLogPath)}\"");
            sb.AppendLine("    goto rollback");
            sb.AppendLine(")");
        }

        // Шаг 3: маркер успеха и запуск обновлённой программы
        sb.AppendLine("echo [3/3] Update applied successfully. Starting updated application...");
        sb.AppendLine($"if exist \"{EscapeBatchPath(failedMarkerPath)}\" del /f /q \"{EscapeBatchPath(failedMarkerPath)}\" 2>nul");
        sb.AppendLine($"echo {currentVer} {targetVersion} > \"{EscapeBatchPath(markerPath)}\"");
        sb.AppendLine($"echo [%DATE% %TIME%] [bat] Update applied: v{currentVer} -^> v{targetVersion} >> \"{EscapeBatchPath(updateLogPath)}\"");
        sb.AppendLine($"start \"\" \"{EscapeBatchPath(exePath)}\" --seamless-update \"{EscapeBatchPath(stateFilePath)}\"");
        sb.AppendLine($"rd /s /q \"{EscapeBatchPath(stagingFolder)}\" 2>nul");
        sb.AppendLine("exit /b 0");

        // Обработка сбоя: атомарный откат к резервной копии
        sb.AppendLine(":rollback");
        sb.AppendLine("echo ========================================================");
        sb.AppendLine("echo CRITICAL: Update failed! Rolling back to previous version...");
        sb.AppendLine("echo ========================================================");
        sb.AppendLine($"echo [%DATE% %TIME%] [bat] ROLLBACK: copy failed, restoring v{currentVer} >> \"{EscapeBatchPath(updateLogPath)}\"");

        foreach (var file in files)
        {
            string relative = file[(stagingFolder.Length + 1)..];
            string dest = Path.Combine(currentDir, relative);
            string backupDest = Path.Combine(backupFolder, relative);

            sb.AppendLine($"if exist \"{EscapeBatchPath(backupDest)}\" (");
            sb.AppendLine($"    copy /Y \"{EscapeBatchPath(backupDest)}\" \"{EscapeBatchPath(dest)}\" >nul");
            sb.AppendLine(") else (");
            sb.AppendLine($"    if exist \"{EscapeBatchPath(dest)}\" del /f /q \"{EscapeBatchPath(dest)}\" 2>nul");
            sb.AppendLine(")");
        }

        sb.AppendLine("echo Rollback completed. Starting previous version...");
        sb.AppendLine($"echo [%DATE% %TIME%] [bat] ROLLBACK completed >> \"{EscapeBatchPath(updateLogPath)}\"");
        sb.AppendLine($"echo {currentVer} {targetVersion} > \"{EscapeBatchPath(failedMarkerPath)}\"");
        sb.AppendLine($"start \"\" \"{EscapeBatchPath(exePath)}\"");
        sb.AppendLine("exit /b 1");

        // Подпрограмма безопасного копирования с повторными попытками
        sb.AppendLine(":safe_copy");
        sb.AppendLine("set \"src=%~1\"");
        sb.AppendLine("set \"dst=%~2\"");
        sb.AppendLine("set retries=0");
        sb.AppendLine(":copy_retry");
        sb.AppendLine("copy /Y \"%src%\" \"%dst%\" >nul 2>&1");
        sb.AppendLine("if \"%ERRORLEVEL%\"==\"0\" exit /b 0");
        sb.AppendLine("set /a retries+=1");
        sb.AppendLine("if %retries% geq 10 exit /b 1");
        sb.AppendLine("timeout /t 1 /nobreak >nul");
        sb.AppendLine("goto copy_retry");

        File.WriteAllText(batPath, sb.ToString(), new System.Text.UTF8Encoding(false));
        UpdateLog.Info("apply", $"Скрипт применения записан: {batPath} ({new FileInfo(batPath).Length} байт)");

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{batPath}\"",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true
        };

        if (needsElevation) psi.Verb = "runas";

        try
        {
            using var scriptProcess = Process.Start(psi);
            UpdateLog.Info("apply",
                $"Скрипт запущен (cmd pid={scriptProcess?.Id}); " +
                $"повышение прав: {(needsElevation ? "запрошено" : "не требуется")}");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // Отказ в UAC приходит именно так, и это НЕ ошибка обновления:
            // пользователь просто не дал права. Раньше вылетало
            // «Отказано в доступе» без пояснения, и обновление выглядело сломанным.
            if (ex.NativeErrorCode == 1223)
            {
                UpdateLog.Warn("apply", "UAC: пользователь отклонил повышение прав; обновление не применено");
                throw new UACDeclinedException(
                    "Обновление установлено в Program Files, поэтому для его применения нужны права администратора. " +
                    "Запустите Motion Commander от имени администратора и повторите обновление, " +
                    "либо выберите при установке режим «только для меня».", ex);
            }
            UpdateLog.Error("apply", $"Не удалось запустить скрипт обновления: {ex.Message}");
            throw new IOException($"Не удалось запустить применение обновления: {ex.Message}", ex);
        }

        // Закрываем приложение только после успешного запуска скрипта обновления:
        // Скрываем все окна немедленно, чтобы интерфейс закрылся для пользователя,
        // и гарантированно выходим из процесса, освобождая блокировки файлов.
        Application.Current?.Dispatcher?.Invoke(() =>
        {
            try
            {
                foreach (Window win in Application.Current.Windows)
                {
                    try { win.Hide(); } catch { }
                }
            }
            catch { }
            try
            {
                Application.Current.Shutdown();
            }
            catch { }
        });

        _ = Task.Run(async () =>
        {
            await Task.Delay(2000);
            try { Environment.Exit(0); } catch { }
        });
    }

    /// <summary>
    /// Экранирует путь для вставки в командный файл.
    /// </summary>
    /// <remarks>
    /// Внутри двойных кавычек cmd всё равно раскрывает <c>%VAR%</c>.
    /// Профиль пользователя вида <c>C:\Users\100% Ready</c> превращал
    /// скрипт обновления в мусор: путь обрезался, копирование падало,
    /// и обновление откатывалось без внятной причины. Удвоение
    /// процента — единственное, что нужно внутри кавычек: символы
    /// <c>&amp; | &lt; &gt; ( )</c> там литеральны.
    /// </remarks>
    public static string EscapeBatchPath(string? path) =>
        (path ?? string.Empty).Replace("%", "%%");

    /// <summary>
    /// Определяет версию, в которую ведёт подготовленное обновление.
    /// </summary>
    /// <remarks>
    /// Для патча версия берётся из patch_manifest.json, для полного
    /// пакета манифеста нет — тогда читается версия файла сборки.
    /// Без этого маркер после полного обновления содержал бы «?»,
    /// и проверка «версия действительно новая» не работала бы.
    /// </remarks>
    private static string TryReadTargetVersion(string stagingFolder)
    {
        string? fromManifest = null;
        try
        {
            string manifest = Path.Combine(stagingFolder, "patch_manifest.json");
            if (File.Exists(manifest))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                if (doc.RootElement.TryGetProperty("targetVersion", out var tv))
                    fromManifest = tv.GetString();
            }
        }
        catch
        {
            fromManifest = null;
        }

        if (!string.IsNullOrWhiteSpace(fromManifest) && fromManifest != "?")
            return NormalizeVersion(fromManifest);

        foreach (string candidate in new[] { "Win11CopyDialog.dll", "motion.exe", "Win11CopyDialog.exe" })
        {
            string path = Path.Combine(stagingFolder, candidate);
            if (!File.Exists(path)) continue;

            try
            {
                var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                string raw = info.FileVersion ?? info.ProductVersion ?? "";
                if (!string.IsNullOrWhiteSpace(raw))
                    return NormalizeVersion(raw);
            }
            catch
            {
                // Попробуем следующий файл: версия нужна для подтверждения,
                // но её отсутствие не должно срывать обновление.
            }
        }

        return "?";
    }

    /// <summary>
    /// Проверяет, что распакованный пакет действительно можно применять.
    /// </summary>
    /// <remarks>
    /// <para>Две проверки, обе — до единой записи в каталог программы.</para>
    ///
    /// <para>1. <b>Версия.</b> Полный пакет не содержит patch_manifest.json,
    /// поэтому раньше его версия узнавалась только после замены файлов —
    /// маркер писал «обновление применено», а следующий запуск сообщал
    /// «не подтвердилось». Если CDN отдавал архив предыдущего релиза,
    /// программа молча откатывалась на него. Теперь архив, ведущий не в
    /// ту версию, отвергается до применения.</para>
    ///
    /// <para>2. <b>Состав.</b> Пакет без исполняемого файла программы
    /// заменял бы библиотеки, оставляя старый exe: запуск после такого
    /// «успеха» невозможен, а откат уже некому выполнить.</para>
    ///
    /// <para>Если версию определить не удалось (нестандартная сборка),
    /// проверка не блокирует обновление: SHA-256 архива уже подтверждён,
    /// и молчаливое «не могу прочитать» не должно запрещать апдейт.
    /// Итог проверки всегда попадает в журнал.</para>
    /// </remarks>
    public static void ValidateStagedPackage(string stagingFolder, string? expectedTargetVersion)
    {
        if (!string.IsNullOrWhiteSpace(expectedTargetVersion))
        {
            string staged = TryReadTargetVersion(stagingFolder);
            if (staged != "?" && CompareVersions(staged, expectedTargetVersion) != 0)
            {
                UpdateLog.Error("staging",
                    $"Архив ведёт в v{staged}, а ожидалась v{expectedTargetVersion}: применение отменено");
                throw new PatchIncompatibleException(
                    $"Скачанный архив ведёт в версию v{staged}, а ожидалась v{expectedTargetVersion}. " +
                    "Скорее всего, сервер раздачи отдал файл другого релиза. " +
                    "Файлы программы не изменены; повторите обновление через минуту.",
                    GetCurrentVersion(), staged, expectedTargetVersion);
            }

            UpdateLog.Info("staging",
                staged == "?"
                    ? "Версию архива определить не удалось; целостность подтверждена контрольной суммой"
                    : $"Версия архива подтверждена: v{staged}");
        }

        string[] launchers = { "motion.exe", "Win11CopyDialog.exe" };
        bool hasLauncher = launchers.Any(name =>
            File.Exists(Path.Combine(stagingFolder, name)));
        if (!hasLauncher)
        {
            UpdateLog.Error("staging",
                "В пакете нет исполняемого файла программы: применение отменено до замены файлов");
            throw new IOException(
                "В скачанном архиве нет исполняемого файла программы (motion.exe / Win11CopyDialog.exe). " +
                "Применять такой пакет нельзя: программа перестала бы запускаться. " +
                "Файлы не изменены; скачайте пакет повторно или установите версию установщиком.");
        }
    }

    /// <summary>Приводит «3.8.52.0» к «3.8.52», чтобы сравнение версий не врало.</summary>
    public static string NormalizeVersion(string version)
    {
        string trimmed = (version ?? string.Empty).Trim().TrimStart('v', 'V');
        if (Version.TryParse(trimmed, out var parsed) && parsed.Build >= 0)
            return $"{parsed.Major}.{parsed.Minor}.{parsed.Build}";
        return trimmed;
    }

    /// <summary>
    /// Проверяет возможность создать файл в каталоге.
    ///
    /// <para>Используется реальная запись, а не права доступа из ACL:
    /// UAC, антивирус и дисковая квота могут запретить запись даже при
    /// формально разрешённых правах, и только настоящая попытка это видит.</para>
    /// </summary>
    public static bool IsDirectoryWritable(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return false;

        string probe = Path.Combine(directory, $"__perm_probe_{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
                fs.WriteByte(0);
            }
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
        finally
        {
            try { if (File.Exists(probe)) File.Delete(probe); } catch { }
        }
    }

    private static void CopyFilesRecursively(string sourcePath, string targetPath)
    {
        foreach (string dirPath in Directory.GetDirectories(sourcePath, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dirPath.Replace(sourcePath, targetPath));
        }

        foreach (string newPath in Directory.GetFiles(sourcePath, "*.*", SearchOption.AllDirectories))
        {
            File.Copy(newPath, newPath.Replace(sourcePath, targetPath), true);
        }
    }
}
