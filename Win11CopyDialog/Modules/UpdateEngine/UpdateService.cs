using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace Win11CopyDialog.Modules.UpdateEngine;

public sealed class UpdateInfo
{
    public string CurrentVersion { get; set; } = "3.0.0";
    public string LatestVersion { get; set; } = "3.0.0";
    public bool IsUpdateAvailable { get; set; }
    public string ReleaseDate { get; set; } = "";
    public List<string> Changelog { get; set; } = new();
    public string DownloadUrl { get; set; } = "";
    public string InstallerUrl { get; set; } = "";
    public string ErrorMessage { get; set; } = "";
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

            if (root.TryGetProperty("downloadUrl", out var dlProp))
            {
                info.DownloadUrl = dlProp.GetString() ?? "";
            }

            if (root.TryGetProperty("installerUrl", out var instProp))
            {
                info.InstallerUrl = instProp.GetString() ?? "";
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

            info.IsUpdateAvailable = IsNewerVersion(currentVerStr, info.LatestVersion);
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

    public static bool IsNewerVersion(string currentStr, string latestStr)
    {
        if (Version.TryParse(currentStr, out var current) && Version.TryParse(latestStr, out var latest))
        {
            return latest > current;
        }
        return string.Compare(latestStr, currentStr, StringComparison.OrdinalIgnoreCase) > 0;
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

        string fileName = Path.GetFileName(new Uri(downloadUrl).LocalPath);
        if (string.IsNullOrEmpty(fileName)) fileName = "MotionCommander-Update.zip";
        string targetFilePath = Path.Combine(tempFolder, fileName);

        using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long totalBytes = response.Content.Headers.ContentLength ?? -1L;
        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(targetFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        byte[] buffer = new byte[81920];
        long totalBytesRead = 0;
        int bytesRead;
        var sw = Stopwatch.StartNew();

        while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            totalBytesRead += bytesRead;

            if (progress != null)
            {
                int percent = totalBytes > 0 ? (int)((totalBytesRead * 100) / totalBytes) : 0;
                double speed = sw.Elapsed.TotalSeconds > 0 ? (totalBytesRead / (1024.0 * 1024.0)) / sw.Elapsed.TotalSeconds : 0;
                progress.Report((totalBytesRead, totalBytes, percent, speed));
            }
        }

        return targetFilePath;
    }

    public static async Task<string> PrepareStagingAsync(string zipPath, CancellationToken ct = default)
    {
        string currentDir = AppDomain.CurrentDomain.BaseDirectory;
        string stagingDir = Path.Combine(currentDir, "staging");
        if (Directory.Exists(stagingDir))
        {
            Directory.Delete(stagingDir, true);
        }
        Directory.CreateDirectory(stagingDir);

        await Task.Run(() => System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, stagingDir, true), ct);
        return stagingDir;
    }

    public static void ApplySeamlessUpdate(string stagingFolder, AppState currentState)
    {
        string currentDir = AppDomain.CurrentDomain.BaseDirectory;
        string stateFilePath = Path.Combine(currentDir, "app_state.json");
        string json = JsonSerializer.Serialize(currentState);
        File.WriteAllText(stateFilePath, json);

        // Write a bat-updater that runs AFTER this process exits
        // This avoids all file-lock issues — we never touch running files
        string newExePath = Path.Combine(currentDir, "Win11CopyDialog.exe");
        string batPath = Path.Combine(Path.GetTempPath(), "mc_update.bat");
        int currentPid = Process.GetCurrentProcess().Id;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine($"echo Waiting for Motion Commander to close...");
        sb.AppendLine($":waitloop");
        sb.AppendLine($"tasklist /FI \"PID eq {currentPid}\" 2>NUL | find /I \"{currentPid}\" >NUL");
        sb.AppendLine($"if \"%ERRORLEVEL%\"==\"0\" (timeout /t 1 /nobreak >nul && goto waitloop)");
        sb.AppendLine($"echo Copying update files...");

        // Generate copy commands for each file in staging
        foreach (var file in Directory.GetFiles(stagingFolder, "*", SearchOption.AllDirectories))
        {
            string relative = file.Substring(stagingFolder.Length).TrimStart('\\', '/');
            string dest = Path.Combine(currentDir, relative);
            string destDir = Path.GetDirectoryName(dest) ?? currentDir;
            sb.AppendLine($"if not exist \"{destDir}\" mkdir \"{destDir}\"");
            sb.AppendLine($"copy /Y \"{file}\" \"{dest}\"");
        }

        sb.AppendLine($"echo Starting updated application...");
        sb.AppendLine($"start \"\" \"{newExePath}\" --seamless-update \"{stateFilePath}\"");
        sb.AppendLine($"del \"%~f0\""); // self-delete the bat

        File.WriteAllText(batPath, sb.ToString(), System.Text.Encoding.ASCII);

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{batPath}\"",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true
        };
        Process.Start(psi);

        // Now safely close current application
        Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
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
