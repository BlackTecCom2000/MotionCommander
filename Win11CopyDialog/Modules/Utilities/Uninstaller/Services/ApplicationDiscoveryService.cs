using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Win11CopyDialog.Modules.Utilities.Uninstaller.Models;

namespace Win11CopyDialog.Modules.Utilities.Uninstaller.Services
{
    public class ApplicationDiscoveryService
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        private const uint SHGFI_ICON = 0x100;
        private const uint SHGFI_LARGEICON = 0x0;

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);
        private static readonly string[] SystemPublishers = new[]
        {
            "Microsoft Corporation",
            "Intel Corporation",
            "Advanced Micro Devices, Inc.",
            "NVIDIA Corporation",
            "Realtek Semiconductor Corp.",
            "Realtek"
        };

        private static ImageSource? _defaultAppIcon;

        public List<InstalledApplication> GetInstalledApplications()
        {
            var apps = new List<InstalledApplication>();

            // 1. Scan HKLM 64-bit
            ScanHive(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", "64-bit", apps);

            // 2. Scan HKLM 32-bit (WOW6432Node)
            ScanHive(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", "32-bit", apps);

            // 3. Scan HKCU (Current User)
            ScanHive(RegistryHive.CurrentUser, RegistryView.Default, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit", apps);

            // Deduplicate by DisplayName, keeping best entry (prefer non-empty install location or size)
            var deduplicated = apps
                .Where(a => !string.IsNullOrWhiteSpace(a.DisplayName) && (!string.IsNullOrWhiteSpace(a.UninstallString) || a.IsOrphaned))
                .GroupBy(a => a.DisplayName.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(x => x.SizeBytes).ThenByDescending(x => x.InstallLocationExists).First())
                .OrderBy(a => a.DisplayName)
                .ToList();

            // Load icons asynchronously in background so loading returns immediately
            _ = Task.Run(() => PopulateIconsAndSizes(deduplicated));

            return deduplicated;
        }

        private void ScanHive(RegistryHive hive, RegistryView view, string subKeyPath, string arch, List<InstalledApplication> apps)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstallKey = baseKey.OpenSubKey(subKeyPath);
                if (uninstallKey == null) return;

                foreach (var subKeyName in uninstallKey.GetSubKeyNames())
                {
                    try
                    {
                        using var appKey = uninstallKey.OpenSubKey(subKeyName);
                        if (appKey == null) continue;

                        var systemComponent = appKey.GetValue("SystemComponent") as int?;
                        var parentKeyName = appKey.GetValue("ParentKeyName") as string;
                        if (!string.IsNullOrEmpty(parentKeyName)) continue; // Skip updates/patches

                        var displayName = appKey.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(displayName)) continue;

                        var uninstallString = appKey.GetValue("UninstallString") as string ?? 
                                              appKey.GetValue("QuietUninstallString") as string ?? string.Empty;

                        var quietUninstallString = appKey.GetValue("QuietUninstallString") as string ?? string.Empty;
                        var modifyPath = appKey.GetValue("ModifyPath") as string ?? string.Empty;
                        var helpLink = appKey.GetValue("HelpLink") as string ?? 
                                       appKey.GetValue("URLInfoAbout") as string ?? 
                                       appKey.GetValue("URLUpdateInfo") as string ?? string.Empty;

                        var publisher = appKey.GetValue("Publisher") as string ?? string.Empty;
                        var displayVersion = appKey.GetValue("DisplayVersion") as string ?? string.Empty;
                        var installDateRaw = appKey.GetValue("InstallDate") as string ?? string.Empty;
                        var installLocation = appKey.GetValue("InstallLocation") as string ?? string.Empty;
                        var displayIcon = appKey.GetValue("DisplayIcon") as string ?? string.Empty;

                        bool isMsi = !string.IsNullOrEmpty(uninstallString) && 
                                     uninstallString.IndexOf("msiexec", StringComparison.OrdinalIgnoreCase) >= 0;

                        // Parse date
                        DateTime? parsedDate = ParseInstallDate(installDateRaw);

                        // Check install location existence
                        bool locationExists = !string.IsNullOrWhiteSpace(installLocation) && Directory.Exists(installLocation);

                        // Compute or read size
                        long sizeBytes = -1;
                        var estimatedSize = appKey.GetValue("EstimatedSize") as int?;
                        if (estimatedSize.HasValue && estimatedSize.Value > 0)
                        {
                            sizeBytes = (long)estimatedSize.Value * 1024L;
                        }

                        string formattedSize = FormatBytes(sizeBytes);

                        bool isSystem = (systemComponent == 1) || DetermineIfSystemComponent(displayName, publisher);

                        // Check if orphaned
                        bool isOrphaned = false;
                        if (!string.IsNullOrWhiteSpace(uninstallString))
                        {
                            string exePath = ExtractExecutablePath(uninstallString);
                            if (!string.IsNullOrEmpty(exePath) && !File.Exists(exePath) && !locationExists && !isMsi)
                            {
                                isOrphaned = true;
                            }
                        }

                        var app = new InstalledApplication
                        {
                            DisplayName = displayName.Trim(),
                            DisplayVersion = displayVersion.Trim(),
                            Publisher = publisher.Trim(),
                            InstallDate = installDateRaw,
                            InstallDateParsed = parsedDate,
                            InstallLocation = installLocation.Trim(),
                            InstallLocationExists = locationExists,
                            UninstallString = uninstallString.Trim(),
                            QuietUninstallString = quietUninstallString.Trim(),
                            ModifyPath = modifyPath.Trim(),
                            HelpLink = helpLink.Trim(),
                            RegistryKeyPath = $@"{hive}\{subKeyPath}\{subKeyName}",
                            DisplayIcon = displayIcon.Trim(),
                            SizeBytes = sizeBytes,
                            EstimatedSize = formattedSize,
                            IsSystemComponent = isSystem,
                            IsMsi = isMsi,
                            IsOrphaned = isOrphaned,
                            Architecture = arch
                        };

                        apps.Add(app);
                    }
                    catch
                    {
                        // Ignore individual key read errors
                    }
                }
            }
            catch
            {
                // Ignore base hive read errors
            }
        }

        private static DateTime? ParseInstallDate(string rawDate)
        {
            if (string.IsNullOrWhiteSpace(rawDate)) return null;

            rawDate = rawDate.Trim();

            // Try "yyyyMMdd"
            if (rawDate.Length == 8 && DateTime.TryParseExact(rawDate, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d1))
                return d1;

            if (DateTime.TryParse(rawDate, CultureInfo.CurrentCulture, DateTimeStyles.None, out var d2))
                return d2;

            if (DateTime.TryParse(rawDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d3))
                return d3;

            return null;
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "—";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F0} КБ";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} МБ";
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} ГБ";
        }

        private static string ExtractExecutablePath(string commandLine)
        {
            if (string.IsNullOrWhiteSpace(commandLine)) return string.Empty;
            string trimmed = commandLine.Trim();

            if (trimmed.StartsWith("\""))
            {
                int endQuote = trimmed.IndexOf("\"", 1);
                if (endQuote > 0)
                {
                    return trimmed.Substring(1, endQuote - 1);
                }
            }

            int firstSpace = trimmed.IndexOf(" ");
            if (firstSpace > 0)
            {
                return trimmed.Substring(0, firstSpace);
            }

            return trimmed;
        }

        private static void PopulateIconsAndSizes(List<InstalledApplication> apps)
        {
            foreach (var app in apps)
            {
                try
                {
                    // 1. Calculate folder size if size was not provided by registry
                    if (app.SizeBytes <= 0 && app.InstallLocationExists)
                    {
                        try
                        {
                            long dirSize = 0;
                            var di = new DirectoryInfo(app.InstallLocation);
                            foreach (var fi in di.EnumerateFiles("*", SearchOption.AllDirectories))
                            {
                                dirSize += fi.Length;
                            }
                            if (dirSize > 0)
                            {
                                app.SizeBytes = dirSize;
                                app.EstimatedSize = FormatBytes(dirSize);
                            }
                        }
                        catch { }
                    }

                    // 2. Extract Icon
                    var icon = ExtractIcon(app);
                    if (icon != null)
                    {
                        Application.Current?.Dispatcher.BeginInvoke(() =>
                        {
                            app.Icon = icon;
                        });
                    }
                }
                catch { }
            }
        }

        private static ImageSource? ExtractIcon(InstalledApplication app)
        {
            string targetPath = string.Empty;

            // 1. Try DisplayIcon
            if (!string.IsNullOrWhiteSpace(app.DisplayIcon))
            {
                string iconPath = app.DisplayIcon.Trim('\"', ' ');
                int commaIdx = iconPath.IndexOf(',');
                if (commaIdx > 0)
                {
                    iconPath = iconPath.Substring(0, commaIdx).Trim();
                }
                if (File.Exists(iconPath))
                {
                    targetPath = iconPath;
                }
            }

            // 2. Try InstallLocation exe
            if (string.IsNullOrEmpty(targetPath) && app.InstallLocationExists)
            {
                try
                {
                    var exe = Directory.EnumerateFiles(app.InstallLocation, "*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
                    if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                    {
                        targetPath = exe;
                    }
                }
                catch { }
            }

            // 3. Try UninstallString
            if (string.IsNullOrEmpty(targetPath) && !string.IsNullOrWhiteSpace(app.UninstallString))
            {
                string exe = ExtractExecutablePath(app.UninstallString);
                if (File.Exists(exe))
                {
                    targetPath = exe;
                }
            }

            if (!string.IsNullOrEmpty(targetPath) && File.Exists(targetPath))
            {
                try
                {
                    var shinfo = new SHFILEINFO();
                    SHGetFileInfo(targetPath, 0, ref shinfo, (uint)Marshal.SizeOf(shinfo), SHGFI_ICON | SHGFI_LARGEICON);
                    if (shinfo.hIcon != IntPtr.Zero)
                    {
                        var bitmapSource = Imaging.CreateBitmapSourceFromHIcon(
                            shinfo.hIcon,
                            Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions());
                        bitmapSource.Freeze();
                        DestroyIcon(shinfo.hIcon);
                        return bitmapSource;
                    }
                }
                catch { }
            }

            return GetDefaultIcon();
        }

        private static ImageSource GetDefaultIcon()
        {
            if (_defaultAppIcon != null) return _defaultAppIcon;

            try
            {
                var geometry = Geometry.Parse("M19 3H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2zm-7 14l-5-5 1.41-1.41L12 14.17l7.59-7.59L21 8l-9 9z");
                var group = new DrawingGroup();
                group.Children.Add(new GeometryDrawing(new SolidColorBrush(System.Windows.Media.Color.FromRgb(59, 130, 246)), null, geometry));
                var image = new DrawingImage(group);
                image.Freeze();
                _defaultAppIcon = image;
                return image;
            }
            catch
            {
                return null!;
            }
        }

        private bool DetermineIfSystemComponent(string name, string publisher)
        {
            var lowerName = name.ToLowerInvariant();
            var lowerPublisher = publisher.ToLowerInvariant();

            if (SystemPublishers.Any(p => lowerPublisher.Contains(p.ToLowerInvariant())))
            {
                if (lowerName.Contains("c++") || lowerName.Contains("redistributable") || 
                    lowerName.Contains("framework") || lowerName.Contains("driver") ||
                    lowerName.Contains("update") || lowerName.Contains("runtime") ||
                    lowerName.Contains("directx"))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
