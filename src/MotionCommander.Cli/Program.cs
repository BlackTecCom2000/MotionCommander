using System.Diagnostics;
using MotionCommander.Core.Archive;
using MotionCommander.Core.Common;
using MotionCommander.Core.Models;
using MotionCommander.Core.Storage;
using MotionCommander.Core.Streaming;

namespace MotionCommander.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            PrintBanner();
            PrintHelp();
            return 0;
        }

        string cmd = args[0].ToLowerInvariant();

        try
        {
            return cmd switch
            {
                "copy" or "cp" => await HandleCopyAsync(args),
                "disks" or "lsblk" or "storage" => await HandleDisksAsync(),
                "smart" => await HandleSmartAsync(args),
                "bench" or "benchmark" => await HandleBenchAsync(args),
                "zip" or "compress" => await HandleZipAsync(args),
                "extract" or "unzip" or "x" => await HandleExtractAsync(args),
                "info" or "sysinfo" or "status" => HandleInfo(),
                "version" or "-v" or "--version" => HandleVersion(),
                _ => HandleUnknownCommand(cmd)
            };
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n[ERROR] Ошибка выполнения: {ex.Message}");
            Console.ResetColor();
            return 1;
        }
    }

    private static void PrintBanner()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
   __  ___     __  _              _____                                     __         
  /  |/  /__  / /_(_)__  ___     / ___/__  __ _  __ _  ___ ____  ___  ___ ____/ /__ ____
 / /|_/ / _ \/ __/ / _ \/ _ \   / /__/ _ \/  ' \/  ' \/ _ `/ _ \/ _ \/ -_) __/  '_// __/
/_/  /_/\___/\__/_/\___/_//_/   \___/\___/_/_/_/_/_/_/\_,_/_//_/_//_/\__/\__/_/\_\/_/   
");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  Кроссплатформенная экосистема передачи данных и контроля накопителей");
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine($"  Версия: v3.0.0 | Платформа: {PlatformDetector.OsName} ({PlatformDetector.Architecture})");
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  Разработчик и автор: BlackTecCom - Jaborov Daler (MIT License)");
        Console.ResetColor();
        Console.WriteLine();
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Использование: motion <команда> [параметры]\n");
        Console.WriteLine("Команды:");
        Console.WriteLine("  copy <src> <dst>          Сверхскоростное потоковое копирование (Full Duplex Pipeline)");
        Console.WriteLine("  disks                     Аудит физических накопителей (NVMe, SSD, HDD, USB) и разделов");
        Console.WriteLine("  smart [диск]              Отчет S.M.A.R.T., температура и остаточный ресурс ячеек");
        Console.WriteLine("  bench [путь]              Аппаратный бенчмарк скорости чтения/записи и IOPS");
        Console.WriteLine("  zip <архив.zip> <файлы>   Создание многопоточного ZIP-архива");
        Console.WriteLine("  extract <архив> [папка]   Распаковка архивов (7z, zip, tar, gz, rar)");
        Console.WriteLine("  info                      Информация о системе, ядре, процессоре и памяти");
        Console.WriteLine("  version                   Номер версии и лицензия");
        Console.WriteLine();
    }

    private static async Task<int> HandleCopyAsync(string[] args)
    {
        if (args.Length < 3)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Ошибка: укажите источник и приёмник. Пример: motion copy source.iso dest.iso");
            Console.ResetColor();
            return 1;
        }

        string src = Path.GetFullPath(args[1]);
        string dst = Path.GetFullPath(args[2]);

        if (!File.Exists(src))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Файл источника не найден: {src}");
            Console.ResetColor();
            return 1;
        }

        long fileLen = new FileInfo(src).Length;
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"\n[STREAMING PIPELINE] Запуск копирования:");
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine($"  Откуда:  {src}");
        Console.WriteLine($"  Куда:    {dst}");
        Console.WriteLine($"  Размер:  {StorageDiskInfo.FormatBytes(fileLen)}");
        Console.ResetColor();

        var sw = Stopwatch.StartNew();
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        int lastPercent = -1;
        await StreamingPipeline.CopyStreamPipelineAsync(
            src, 
            dst, 
            bufferSize: BufferPool.DefaultBufferSize,
            onTelemetry: t =>
            {
                int p = fileLen > 0 ? (int)((t.BytesTransferred * 100) / fileLen) : 0;
                double speedMB = t.InstantThroughputBytesPerSec / (1024 * 1024);
                if (p != lastPercent || t.BytesTransferred >= fileLen)
                {
                    lastPercent = p;
                    string bar = new string('█', p / 5).PadRight(20, '░');
                    Console.Write($"\r  [{bar}] {p,3}% | {speedMB,6:F1} МБ/с | Записано: {StorageDiskInfo.FormatBytes(t.BytesTransferred)}    ");
                }
            },
            ct: cts.Token);

        sw.Stop();
        double avgSpeed = sw.Elapsed.TotalSeconds > 0 ? (fileLen / (1024.0 * 1024.0)) / sw.Elapsed.TotalSeconds : 0;

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\n✔ Копирование завершено успешно за {sw.Elapsed.TotalSeconds:F2} сек! Средняя скорость: {avgSpeed:F1} МБ/с\n");
        Console.ResetColor();
        return 0;
    }

    private static async Task<int> HandleDisksAsync()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"\n[STORAGE TOPOLOGY] Физические накопители ({PlatformDetector.OsName}):\n");
        Console.ResetColor();

        var provider = StorageProviderFactory.Default;
        var disks = await provider.GetPhysicalDisksAsync();

        if (disks.Count == 0)
        {
            Console.WriteLine("Накопители не обнаружены.");
            return 0;
        }

        foreach (var d in disks)
        {
            string badge = d.MediaType switch
            {
                DiskMediaType.NVMe => "[NVMe PCIe]",
                DiskMediaType.SSD => "[SATA SSD]",
                DiskMediaType.HDD => "[HDD Drive]",
                DiskMediaType.FlashMemory => "[USB Flash]",
                _ => "[Storage]"
            };

            Console.ForegroundColor = d.MediaType == DiskMediaType.NVMe ? ConsoleColor.Cyan : (d.MediaType == DiskMediaType.SSD ? ConsoleColor.Blue : ConsoleColor.DarkYellow);
            Console.Write($"  #{d.Index} {badge,-12} ");
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write($"{d.Model,-32} ");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write($"{d.FormattedSize,10} ");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write($"| Здоровье: {d.HealthPercent}% ({d.HealthGrade}) | {d.TemperatureC:F0}°C");
            Console.ResetColor();
            Console.WriteLine();

            if (d.Partitions.Count > 0)
            {
                foreach (var p in d.Partitions)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"      └─ Раздел #{p.PartitionNumber}: {p.DevicePath} [{p.FileSystem}] {p.FormattedSize} (Свободно: {p.FormattedFree}) {p.MountPoint}");
                    Console.ResetColor();
                }
            }
            Console.WriteLine();
        }

        return 0;
    }

    private static async Task<int> HandleSmartAsync(string[] args)
    {
        int diskIdx = 0;
        if (args.Length > 1 && int.TryParse(args[1], out var idx)) diskIdx = idx;

        var provider = StorageProviderFactory.Default;
        var report = await provider.GetSmartReportAsync(diskIdx);

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"\n[S.M.A.R.T. REPORT] Накопитель #{diskIdx} - {report.Model}:");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  Состояние: {report.HealthPercent}% (Рейтинг: {report.Grade}) | Температура: {report.TemperatureC:F0}°C");
        Console.ResetColor();

        Console.WriteLine("\nРекомендации и диагностика ядра:");
        foreach (var r in report.Recommendations)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  • {r}");
        }
        Console.ResetColor();
        Console.WriteLine();
        return 0;
    }

    private static async Task<int> HandleBenchAsync(string[] args)
    {
        await Task.Yield();
        string target = args.Length > 1 ? args[1] : Path.GetTempPath();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"\n[HARDWARE BENCHMARK] Тестирование накопителя ({target}):");
        Console.ResetColor();

        string testFile = Path.Combine(target, $"motion_bench_{Guid.NewGuid():N}.tmp");
        const int testSize = 256 * 1024 * 1024; // 256 MB
        byte[] buffer = new byte[BufferPool.DefaultBufferSize];
        new Random().NextBytes(buffer);

        // 1. Тест записи
        Console.Write("  1. Последовательная запись (Sequential Write)... ");
        var swWrite = Stopwatch.StartNew();
        using (var fs = new FileStream(testFile, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length, FileOptions.WriteThrough))
        {
            long written = 0;
            while (written < testSize)
            {
                fs.Write(buffer, 0, buffer.Length);
                written += buffer.Length;
            }
        }
        swWrite.Stop();
        double writeSpeed = (testSize / (1024.0 * 1024.0)) / swWrite.Elapsed.TotalSeconds;
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"{writeSpeed:F1} МБ/с");
        Console.ResetColor();

        // 2. Тест чтения
        Console.Write("  2. Последовательное чтение (Sequential Read)... ");
        var swRead = Stopwatch.StartNew();
        using (var fs = new FileStream(testFile, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, FileOptions.SequentialScan))
        {
            long read = 0;
            while (read < testSize)
            {
                int r = fs.Read(buffer, 0, buffer.Length);
                if (r <= 0) break;
                read += r;
            }
        }
        swRead.Stop();
        double readSpeed = (testSize / (1024.0 * 1024.0)) / swRead.Elapsed.TotalSeconds;
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"{readSpeed:F1} МБ/с");
        Console.ResetColor();

        try { File.Delete(testFile); } catch { }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"\n  Индекс скорости накопителя: {Math.Round((readSpeed + writeSpeed) / 2):N0} баллов\n");
        Console.ResetColor();
        return 0;
    }

    private static async Task<int> HandleZipAsync(string[] args)
    {
        if (args.Length < 3)
        {
            Console.WriteLine("Использование: motion zip <archive.zip> <файл1> [файл2] ...");
            return 1;
        }

        string zipPath = args[1];
        var files = args.Skip(2).ToList();

        Console.WriteLine($"Создание архива {zipPath} из {files.Count} элементов...");
        await ArchiveService.CreateZipAsync(files, zipPath);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"✔ Архив успешно создан: {zipPath}");
        Console.ResetColor();
        return 0;
    }

    private static async Task<int> HandleExtractAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Использование: motion extract <archive> [папка]");
            return 1;
        }

        string archivePath = args[1];
        string dest = args.Length > 2 ? args[2] : Directory.GetCurrentDirectory();

        Console.WriteLine($"Распаковка {archivePath} в {dest}...");
        await ArchiveService.ExtractAllAsync(archivePath, dest);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"✔ Распаковка завершена!");
        Console.ResetColor();
        return 0;
    }

    private static int HandleInfo()
    {
        PrintBanner();
        Console.WriteLine("Системная конфигурация:");
        Console.WriteLine($"  ОС:            {PlatformDetector.OsDescription}");
        Console.WriteLine($"  Ядро платформы:{PlatformDetector.OsName}");
        Console.WriteLine($"  Архитектура:   {PlatformDetector.Architecture}");
        Console.WriteLine($"  Ядер CPU:      {PlatformDetector.LogicalCoreCount}");
        Console.WriteLine($"  .NET Среда:    {Environment.Version}");
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("Поддержка автора (Донат фрилансеру / VISA):");
        Console.WriteLine("  • 🇹🇯 Alif Bank VISA: 4444 8888 1022 6013");
        Console.WriteLine("  • 🇹🇯 DC Bank VISA:   4713 3800 2165 1431");
        Console.ResetColor();
        Console.WriteLine();
        return 0;
    }

    private static int HandleVersion()
    {
        Console.WriteLine("Motion Commander v3.0.0");
        Console.WriteLine("Copyright (c) 2026 BlackTecCom - Jaborov Daler. All rights reserved.");
        Console.WriteLine("Licensed under the MIT License.");
        return 0;
    }

    private static int HandleUnknownCommand(string cmd)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Неизвестная команда '{cmd}'. Запустите 'motion --help' для списка команд.");
        Console.ResetColor();
        return 1;
    }
}
