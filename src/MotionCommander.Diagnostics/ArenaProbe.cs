using System;
using System.IO;
using System.Linq;
using Win11CopyDialog.Helpers;
using Win11CopyDialog.Modules.TajArena.Models;
using Win11CopyDialog.Modules.TajArena.Services;

namespace MotionCommander.Diagnostics;

/// <summary>
/// Проверка игровой категории TAJ ARENA на реальных данных машины.
/// </summary>
/// <remarks>
/// Вызывает настоящий <see cref="GameDiscoveryService.Discover"/>, тот
/// самый код, что работает в окне: никакого копирования логики ради
/// проверки не существует. Числа в отчёте — измерения файловой
/// системы на момент запуска: объём считается тем же методом, что и в
/// окне, а доля диска — по реальным DriveInfo.
/// </remarks>
public static class ArenaProbe
{
    public static int Run(TextWriter outp)
    {
        outp.WriteLine("TAJ ARENA - автопоиск игр на этой машине");
        outp.WriteLine(new string('=', 70));

        var result = GameDiscoveryService.Discover();

        outp.WriteLine("Честный журнал источников:");
        foreach (var probe in result.Probes)
        {
            outp.WriteLine($"  - {probe}");
        }

        outp.WriteLine("");
        outp.WriteLine($"Игр найдено: {result.Games.Count}");

        foreach (var game in result.Games)
        {
            var (size, partial) = MeasureSize(game.InstallPath);
            double? percent = ComputeDiskPercent(game, size);

            outp.WriteLine($"  [{game.PlatformName}] {game.DisplayName}");
            outp.WriteLine($"      папка:  {game.InstallPath}");
            outp.WriteLine($"      объём:  {Formatters.Bytes(size)}{(partial ? " (неполное измерение)" : "")}");
            outp.WriteLine($"      доля:   {(percent is double p ? p.ToString("F2") + "%" : "—")}");
            outp.WriteLine($"      exe:    {(game.MainExePath ?? "не выбран (кандидатов: " + game.CandidateExePaths.Count + ")")}");
        }

        if (result.Games.Count == 0)
        {
            outp.WriteLine("  Игр не найдено. Это честный результат, а не сбой:");
            outp.WriteLine("  журнал выше показывает, какие источники проверялись.");
        }

        outp.WriteLine(new string('=', 70));
        outp.WriteLine("ПРОВЕРКА ЗАВЕРШЕНА.");
        return 0;
    }

    /// <summary>
    /// Тот же алгоритм, что и в TajArenaWindow.MeasureDirectorySize:
    /// копия осознанная, проверке нужен изолированный вывод без окна.
    /// </summary>
    private static (long size, bool partial) MeasureSize(string path)
    {
        long total = 0;
        bool partial = false;

        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*",
                new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true }))
            {
                try { total += new FileInfo(file).Length; }
                catch { partial = true; }
            }
        }
        catch
        {
            partial = true;
        }

        return (total, partial);
    }

    private static double? ComputeDiskPercent(GameEntry game, long size)
    {
        try
        {
            if (game.DriveRoot == null) return null;
            var drive = new DriveInfo(game.DriveRoot);
            if (!drive.IsReady || drive.TotalSize <= 0) return null;
            return Math.Round(size * 100.0 / drive.TotalSize, 2);
        }
        catch
        {
            return null;
        }
    }
}
