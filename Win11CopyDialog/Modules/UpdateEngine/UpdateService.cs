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
    public double FullSizeMb { get; set; } = 40.5;
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
    /// </summary>
    public double DownloadSizeMb => (PackageType == UpdatePackageType.Patch && HasPatch)
        ? PatchSizeMb
        : (FullSizeMb > 0 ? FullSizeMb : 40.5);

    /// <summary>
    /// Процент экономии трафика при использовании патча по сравнению с полным пакетом.
    /// </summary>
    public int TrafficSavingPercent
    {
        get
        {
            if (PackageType != UpdatePackageType.Patch || PatchSizeMb <= 0) return 0;
            double full = FullSizeMb > 0 ? FullSizeMb : 40.5;
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
                return saving > 0
                    ? $"Быстрый патч: ~{PatchSizeMb:F1} МБ (экономия {saving}% трафика вместо ~{FullSizeMb:F1} МБ)"
                    : $"Быстрый патч: ~{PatchSizeMb:F1} МБ";
            }
            return $"Полный пакет: ~{DownloadSizeMb:F1} МБ";
        }
    }
}

public static class UpdateService
{
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public const string ManifestUrl = "https://raw.githubusercontent.com/BlackTecCom2000/MotionCommander/main/version.json";
    public const string GitHubReleasesUrl = "https://api.github.com/repos/BlackTecCom2000/MotionCommander/releases/latest";
    public const string GitHubRepoUrl = "https://github.com/BlackTecCom2000/MotionCommander";

    static UpdateService()
    {
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "MotionCommander-AutoUpdater");
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
            // 1. Попытка чтения Manifest URL (быстро, с cache_bypass для мгновенной отдачи после push в репозиторий)
            string urlWithBypass = $"{ManifestUrl}?cache_bypass={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            string json = await _httpClient.GetStringAsync(urlWithBypass, ct);
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

            return info;
        }
        catch (Exception ex)
        {
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
                                    if (dl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                                    {
                                        info.DownloadUrl = dl;
                                        info.InstallerUrl = dl;
                                        break;
                                    }
                                }
                            }
                        }
                        if (string.IsNullOrEmpty(info.DownloadUrl))
                        {
                            info.DownloadUrl = $"{GitHubRepoUrl}/releases/download/{tag}/MotionCommander-Windows-x64.zip";
                        }
                        return info;
                    }
                }
            }
            catch { }

            info.ErrorMessage = ex.Message;
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
                                    if (dl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(item.DownloadUrl))
                                    {
                                        item.DownloadUrl = dl;
                                    }
                                    if (dl.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(item.SetupExeUrl))
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
                            SetupExeUrl = $"https://raw.githubusercontent.com/BlackTecCom2000/MotionCommander/main/dist/MotionCommander-v{verNum}-Setup.exe",
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
                list.Add(new ReleaseVersionItem
                {
                    Version = v,
                    TagName = $"v{v}",
                    Name = $"Motion Commander v{v}",
                    PatchUrl = $"https://raw.githubusercontent.com/BlackTecCom2000/MotionCommander/main/dist/MotionCommander-v{v}-Patch.zip",
                    DownloadUrl = $"https://raw.githubusercontent.com/BlackTecCom2000/MotionCommander/main/dist/MotionCommander-v{v}-Portable.zip",
                    SetupExeUrl = $"https://raw.githubusercontent.com/BlackTecCom2000/MotionCommander/main/dist/MotionCommander-v{v}-Setup.exe",
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

    public static async Task<string> DownloadUpdateAsync(
        string downloadUrl, 
        IProgress<(long bytesRead, long totalBytes, int percent, double speedMBps)>? progress = null, 
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl))
            throw new ArgumentException("Ссылка на загрузку обновления не указана.");

        string tempFolder = Path.Combine(Path.GetTempPath(), "MotionCommander-Update");
        Directory.CreateDirectory(tempFolder);

        string originalFileName = Path.GetFileName(new Uri(downloadUrl).LocalPath);
        if (string.IsNullOrEmpty(originalFileName)) originalFileName = "MotionCommander-Update.zip";
        string targetFilePath = Path.Combine(tempFolder, originalFileName);

        // Уникальный временный файл (.part) изолирует процесс загрузки от внешних блокировок и остаточных файлов
        // ВАЖНО: нельзя писать {Guid.NewGuid():N[..8]} — «N[..8]» уходит в Guid.ToString(format)
        // и всегда бросает FormatException("Format string can be only ..."). Так и происходит:
        // первый полный снимок быстрого патча у пользователей падал всегда.
        // Берём первые 8 символов N-формы после форматирования.
        string uniquePartPath = Path.Combine(tempFolder, $"{originalFileName}.{Guid.NewGuid().ToString("N")[..8]}.part");

        using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long totalBytes = response.Content.Headers.ContentLength ?? -1L;
        string calculatedSha256;

        using (var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256))
        {
            await using (var contentStream = await response.Content.ReadAsStreamAsync(ct))
            await using (var fileStream = new FileStream(uniquePartPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                byte[] buffer = new byte[81920];
                long totalBytesRead = 0;
                int bytesRead;
                var sw = Stopwatch.StartNew();

                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                    sha.AppendData(buffer, 0, bytesRead);
                    totalBytesRead += bytesRead;

                    if (progress != null)
                    {
                        int percent = totalBytes > 0 ? (int)((totalBytesRead * 100) / totalBytes) : 0;
                        double speed = sw.Elapsed.TotalSeconds > 0 ? (totalBytesRead / (1024.0 * 1024.0)) / sw.Elapsed.TotalSeconds : 0;
                        progress.Report((totalBytesRead, totalBytes, percent, speed));
                    }
                }

                await fileStream.FlushAsync(ct);
            }

            calculatedSha256 = Convert.ToHexString(sha.GetHashAndReset());
        }

        // Потоки записи закрыты и освобождены.
        // Целостность проверяется ДО распаковки и запуска.
        await VerifyIntegrityAsync(uniquePartPath, originalFileName, calculatedSha256, ct);

        // Безопасное повышение временного файла до целевого пути с повторными попытками.
        // Если целевой файл заблокирован другим процессом (например, антивирусом или Проводником),
        // возвращаем проверенный путь .part напрямую, чтобы не прерывать обновление.
        string finalPath = await SafePromoteDownloadedFileAsync(uniquePartPath, targetFilePath, ct);
        return finalPath;
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
        string expected = await ReleaseManifest.GetSha256ForAsync(originalFileName, ct);

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
    public static async Task<string> PrepareStagingAsync(string zipPath, CancellationToken ct = default)
    {
        string stagingDir = AppPaths.StagingDirectory;

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

        // Подготовка каталога резервной копии перед заменой файлов
        string backupRoot = AppPaths.BackupDirectory;
        PruneOldBackups(backupRoot, maxToKeep: 3);

        string currentVer = GetCurrentVersion();
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string backupFolder = Path.Combine(backupRoot, $"backup_v{currentVer}_{timestamp}");

        // Скрипт запускается ПОСЛЕ выхода процесса: так снимаются все
        // блокировки на собственные исполняемые файлы.
        string batPath = Path.Combine(AppPaths.StagingDirectory, "mc_update.bat");
        int currentPid = Environment.ProcessId;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("chcp 65001 >nul");
        sb.AppendLine("echo ========================================================");
        sb.AppendLine("echo   Motion Commander — Seamless Update Engine");
        sb.AppendLine("echo ========================================================");
        sb.AppendLine("echo Waiting for Motion Commander to close...");

        // Проверка живого процесса по CSV-выводу tasklist.
        // Ожидаем до 5 секунд, после чего принудительно завершаем зависший процесс,
        // чтобы блокировка исполняемых файлов не сорвала обновление.
        sb.AppendLine("set wait_count=0");
        sb.AppendLine(":waitloop");
        sb.AppendLine($"tasklist /FI \"PID eq {currentPid}\" /NH /FO CSV 2>NUL | findstr /C:\"\"{currentPid}\"\" >NUL");
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
        sb.AppendLine($"if not exist \"{backupFolder}\" mkdir \"{backupFolder}\"");
        sb.AppendLine($"echo Backup v{currentVer} created on %DATE% %TIME% > \"{Path.Combine(backupFolder, "backup_info.txt")}\"");

        foreach (var file in files)
        {
            string relative = file[(stagingFolder.Length + 1)..];
            string dest = Path.Combine(currentDir, relative);
            string backupDest = Path.Combine(backupFolder, relative);
            string backupDestDir = Path.GetDirectoryName(backupDest) ?? backupFolder;

            sb.AppendLine($"if exist \"{dest}\" (");
            sb.AppendLine($"    if not exist \"{backupDestDir}\" mkdir \"{backupDestDir}\"");
            sb.AppendLine($"    copy /Y \"{dest}\" \"{backupDest}\" >nul");
            sb.AppendLine(")");
        }

        // Шаг 2: Копирование обновлённых файлов поверх текущих файлов программы
        sb.AppendLine("echo [2/3] Copying update files in-place...");
        foreach (var file in files)
        {
            string relative = file[(stagingFolder.Length + 1)..];
            string dest = Path.Combine(currentDir, relative);
            string destDir = Path.GetDirectoryName(dest) ?? currentDir;
            sb.AppendLine($"if not exist \"{destDir}\" mkdir \"{destDir}\"");
            sb.AppendLine($"call :safe_copy \"{file}\" \"{dest}\"");
            sb.AppendLine("if errorlevel 1 (");
            sb.AppendLine($"    echo ERROR: Failed to replace {relative}. Triggering rollback!");
            sb.AppendLine("    goto rollback");
            sb.AppendLine(")");
        }

        // Шаг 3: Успешный запуск и очистка
        sb.AppendLine("echo [3/3] Update applied successfully. Starting updated application...");
        sb.AppendLine($"start \"\" \"{exePath}\" --seamless-update \"{stateFilePath}\"");
        sb.AppendLine($"rd /s /q \"{stagingFolder}\" 2>nul");
        sb.AppendLine("exit /b 0");

        // Обработка сбоя: атомарный откат к резервной копии
        sb.AppendLine(":rollback");
        sb.AppendLine("echo ========================================================");
        sb.AppendLine("echo CRITICAL: Update failed! Rolling back to previous version...");
        sb.AppendLine("echo ========================================================");

        foreach (var file in files)
        {
            string relative = file[(stagingFolder.Length + 1)..];
            string dest = Path.Combine(currentDir, relative);
            string backupDest = Path.Combine(backupFolder, relative);

            sb.AppendLine($"if exist \"{backupDest}\" (");
            sb.AppendLine($"    copy /Y \"{backupDest}\" \"{dest}\" >nul");
            sb.AppendLine(") else (");
            sb.AppendLine($"    if exist \"{dest}\" del /f /q \"{dest}\" 2>nul");
            sb.AppendLine(")");
        }

        sb.AppendLine("echo Rollback completed. Starting previous version...");
        sb.AppendLine($"start \"\" \"{exePath}\"");
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

        // Нужно ли повышение привилегий: проверяется реальной записью
        // рядом с программой, а не предположением о пути установки.
        bool needsElevation = !IsDirectoryWritable(currentDir);

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
            Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // Отказ в UAC приходит именно так, и это НЕ ошибка обновления:
            // пользователь просто не дал права. Раньше вылетало
            // «Отказано в доступе» без пояснения, и обновление выглядело сломанным.
            if (ex.NativeErrorCode == 1223)
            {
                throw new UACDeclinedException(
                    "Обновление установлено в Program Files, поэтому для его применения нужны права администратора. " +
                    "Запустите Motion Commander от имени администратора и повторите обновление, " +
                    "либо выберите при установке режим «только для меня».", ex);
            }
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
