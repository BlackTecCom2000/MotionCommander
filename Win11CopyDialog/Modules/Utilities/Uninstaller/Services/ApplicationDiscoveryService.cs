using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;
using Win11CopyDialog.Modules.Utilities.Uninstaller.Models;

namespace Win11CopyDialog.Modules.Utilities.Uninstaller.Services
{
    public class ApplicationDiscoveryService
    {
        private static readonly string[] RegistryKeysToScan = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };

        private static readonly string[] SystemPublishers = new[]
        {
            "Microsoft Corporation",
            "Intel Corporation",
            "Advanced Micro Devices, Inc.",
            "NVIDIA Corporation",
            "Realtek"
        };

        public List<InstalledApplication> GetInstalledApplications()
        {
            var apps = new List<InstalledApplication>();

            // Scan HKLM (All Users)
            foreach (var keyPath in RegistryKeysToScan)
            {
                ScanRegistryKey(Registry.LocalMachine, keyPath, apps);
            }

            // Scan HKCU (Current User)
            ScanRegistryKey(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", apps);

            // Deduplicate and filter out apps with no DisplayName or UninstallString
            var filteredApps = apps
                .Where(a => !string.IsNullOrWhiteSpace(a.DisplayName) && !string.IsNullOrWhiteSpace(a.UninstallString))
                .GroupBy(a => a.DisplayName)
                .Select(g => g.First())
                .OrderBy(a => a.DisplayName)
                .ToList();

            return filteredApps;
        }

        private void ScanRegistryKey(RegistryKey baseKey, string keyPath, List<InstalledApplication> apps)
        {
            try
            {
                using var uninstallKey = baseKey.OpenSubKey(keyPath);
                if (uninstallKey == null) return;

                foreach (var subKeyName in uninstallKey.GetSubKeyNames())
                {
                    try
                    {
                        using var appKey = uninstallKey.OpenSubKey(subKeyName);
                        if (appKey == null) continue;

                        var systemComponent = appKey.GetValue("SystemComponent") as int?;
                        if (systemComponent == 1) continue; // Skip hidden system components

                        var parentKeyName = appKey.GetValue("ParentKeyName") as string;
                        if (!string.IsNullOrEmpty(parentKeyName)) continue; // Skip updates

                        var displayName = appKey.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(displayName)) continue;

                        var uninstallString = appKey.GetValue("UninstallString") as string ?? 
                                              appKey.GetValue("QuietUninstallString") as string;

                        var publisher = appKey.GetValue("Publisher") as string ?? string.Empty;

                        var app = new InstalledApplication
                        {
                            DisplayName = displayName,
                            DisplayVersion = appKey.GetValue("DisplayVersion") as string ?? string.Empty,
                            Publisher = publisher,
                            InstallDate = appKey.GetValue("InstallDate") as string ?? string.Empty,
                            InstallLocation = appKey.GetValue("InstallLocation") as string ?? string.Empty,
                            UninstallString = uninstallString ?? string.Empty,
                            DisplayIcon = appKey.GetValue("DisplayIcon") as string ?? string.Empty,
                            RegistryKeyPath = $@"{baseKey.Name}\{keyPath}\{subKeyName}",
                            IsSystemComponent = DetermineIfSystemComponent(displayName, publisher)
                        };

                        // Optional: Format size
                        var estimatedSize = appKey.GetValue("EstimatedSize") as int?;
                        if (estimatedSize.HasValue)
                        {
                            app.EstimatedSize = $"{estimatedSize.Value / 1024.0:F1} MB";
                        }

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
                // Ignore base key read errors
            }
        }

        private bool DetermineIfSystemComponent(string name, string publisher)
        {
            var lowerName = name.ToLower();
            var lowerPublisher = publisher.ToLower();

            if (SystemPublishers.Any(p => p.ToLower() == lowerPublisher))
            {
                // If it's a Microsoft redistributable, driver, or framework, flag it as system
                if (lowerName.Contains("c++") || lowerName.Contains("redistributable") || 
                    lowerName.Contains("framework") || lowerName.Contains("driver") ||
                    lowerName.Contains("update"))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
