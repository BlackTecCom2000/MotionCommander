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
            // 1. Попытка чтения Manifest URL (быстро, без rate-limit GitHub API)
            string json = await _httpClient.GetStringAsync(ManifestUrl, ct);
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
            // Fallback: если репозиторий только создан или оффлайн
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

    public static void ApplyUpdateAndRestart(string updatePackagePath)
    {
        string currentExe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
        string currentDir = AppDomain.CurrentDomain.BaseDirectory;
        string tempDir = Path.GetDirectoryName(updatePackagePath) ?? Path.GetTempPath();
        string scriptPath = Path.Combine(tempDir, "apply_update.cmd");

        // Автономный командный скрипт для подмены файлов после выхода процесса
        int currentPid = Process.GetCurrentProcess().Id;
        string scriptContent = $@"@echo off
timeout /t 2 /nobreak > nul
:wait_process
tasklist /fi ""PID eq {currentPid}"" | find ""{currentPid}"" > nul
if %ERRORLEVEL% equ 0 (
    timeout /t 1 /nobreak > nul
    goto wait_process
)

echo Обновление Motion Commander...
if ""{Path.GetExtension(updatePackagePath).ToLowerInvariant()}""==""exe"" (
    start """" ""{updatePackagePath}"" /SILENT
    exit /b 0
)

powershell -Command ""Expand-Archive -Path '{updatePackagePath}' -DestinationPath '{currentDir}' -Force""
start """" ""{currentExe}""
del ""%~f0""
exit /b 0
";
        File.WriteAllText(scriptPath, scriptContent);

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{scriptPath}\"",
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        Process.Start(psi);
        Application.Current.Shutdown();
    }
}
