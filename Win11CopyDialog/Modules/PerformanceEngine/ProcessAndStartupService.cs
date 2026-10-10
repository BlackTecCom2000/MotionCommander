using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using Win11CopyDialog.Helpers;

namespace Win11CopyDialog.Modules.PerformanceEngine;

public sealed class ProcessResourceInfo
{
    public int Pid { get; set; }
    public string Name { get; set; } = "";
    public string FriendlyDescription { get; set; } = "";
    public double MemoryMB { get; set; }
    public string MemoryFormatted => $"{MemoryMB:F1} МБ";
    public string Status { get; set; } = "Работает";
    public bool IsSystemCritical { get; set; }
}

public sealed class StartupItemInfo
{
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public string Scope { get; set; } = "Пользователь (HKCU)";
    public string ImpactRating { get; set; } = "Среднее";
    public string ImpactColor { get; set; } = "#F59E0B";
}

public static class ProcessAndStartupService
{
    private static readonly Dictionary<string, (string desc, bool isCritical)> _knownProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        { "explorer", ("Проводник Windows (Рабочий стол и панель задач)", true) },
        { "svchost", ("Хост-процесс системных служб Windows", true) },
        { "csrss", ("Клиент-серверная подсистема времени выполнения", true) },
        { "smss", ("Диспетчер сеанса Windows", true) },
        { "lsass", ("Служба безопасности учетных записей (LSASS)", true) },
        { "services", ("Приложение служб Windows", true) },
        { "winlogon", ("Программа входа в систему Windows", true) },
        { "dwm", ("Диспетчер окон рабочего стола (Desktop Window Manager)", true) },
        { "spoolsv", ("Диспетчер очереди печати", false) },
        { "SearchIndexer", ("Индексатор службы поиска Windows", false) },
        { "chrome", ("Веб-браузер Google Chrome", false) },
        { "msedge", ("Веб-браузер Microsoft Edge", false) },
        { "firefox", ("Веб-браузер Mozilla Firefox", false) },
        { "steam", ("Игровой клиент Steam", false) },
        { "discord", ("Мессенджер Discord", false) },
        { "telegram", ("Мессенджер Telegram Desktop", false) },
        { "code", ("Редактор кода Visual Studio Code", false) },
        { "devenv", ("Среда разработки Visual Studio", false) },
        { "Win11CopyDialog", ("Motion Commander (Текущее приложение)", false) }
    };

    public static List<ProcessResourceInfo> GetTopHeavyProcesses(int limit = 12)
    {
        var list = new List<ProcessResourceInfo>();

        try
        {
            var processes = Process.GetProcesses();
            var grouped = processes
                .GroupBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    long totalWorkingSet = 0;
                    int pid = 0;
                    foreach (var p in g)
                    {
                        try
                        {
                            totalWorkingSet += p.WorkingSet64;
                            if (pid == 0) pid = p.Id;
                        }
                        catch { }
                    }

                    string procName = g.Key;
                    string friendly = _knownProcesses.TryGetValue(procName, out var info)
                        ? info.desc
                        : $"Процесс приложения {procName}.exe";

                    bool isCrit = _knownProcesses.TryGetValue(procName, out var critInfo) && critInfo.isCritical;

                    return new ProcessResourceInfo
                    {
                        Pid = pid,
                        Name = procName,
                        FriendlyDescription = friendly,
                        MemoryMB = Math.Round(totalWorkingSet / (1024.0 * 1024.0), 1),
                        IsSystemCritical = isCrit
                    };
                })
                .OrderByDescending(p => p.MemoryMB)
                .Take(limit)
                .ToList();

            return grouped;
        }
        catch
        {
            return list;
        }
    }

    public static List<StartupItemInfo> GetStartupItems()
    {
        var items = new List<StartupItemInfo>();

        void ReadRunKey(RegistryKey? root, string subKey, string scope)
        {
            if (root == null) return;
            try
            {
                using var key = root.OpenSubKey(subKey, false);
                if (key == null) return;
                foreach (var valName in key.GetValueNames())
                {
                    string cmd = key.GetValue(valName)?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(valName)) continue;

                    string impact = "Среднее";
                    string impactColor = "#F59E0B";

                    string cmdLower = cmd.ToLowerInvariant();
                    if (cmdLower.Contains("steam") || cmdLower.Contains("epic") || cmdLower.Contains("discord") || cmdLower.Contains("chrome"))
                    {
                        impact = "Высокое";
                        impactColor = "#EF4444";
                    }
                    else if (cmdLower.Contains("audio") || cmdLower.Contains("realtek") || cmdLower.Contains("security"))
                    {
                        impact = "Низкое";
                        impactColor = "#10B981";
                    }

                    items.Add(new StartupItemInfo
                    {
                        Name = valName,
                        Command = cmd,
                        Scope = scope,
                        ImpactRating = impact,
                        ImpactColor = impactColor
                    });
                }
            }
            catch { }
        }

        ReadRunKey(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "Текущий пользователь (HKCU)");
        ReadRunKey(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run", "Все пользователи (HKLM)");

        return items;
    }

    public static bool OpenWindowsStartupSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool OpenTaskManager()
    {
        try
        {
            Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool OpenServicesManager()
    {
        try
        {
            Process.Start(new ProcessStartInfo("services.msc") { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
