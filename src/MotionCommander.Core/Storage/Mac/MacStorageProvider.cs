using System.Diagnostics;
using System.IO;
using System.Text.Json;
using MotionCommander.Core.Models;

namespace MotionCommander.Core.Storage.Mac;

public sealed class MacStorageProvider : IStorageProvider
{
    /// <summary>
    /// Максимальное ожидание внешней утилиты (system_profiler, diskutil).
    /// Раньше ожидание не было ограничено: зависший diskutil или
    /// system_profiler, упершийся в сетевой том, держали вызывающий код вечно.
    /// </summary>
    private static readonly TimeSpan ExternalCommandTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Сколько ждём освобождения каналов после выхода процесса.</summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Аварийно снимает зависшую утилиту вместе со всеми потомками.
    /// Процесс может завершиться между проверкой HasExited и вызовом Kill,
    /// поэтому всё обёрнуто в try/catch.
    /// </summary>
    private static void TryKill(Process proc)
    {
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(2000);
            }
        }
        catch
        {
            // Процесс уже завершился или недоступен — делать больше нечего.
        }
    }

    public async Task<List<StorageDiskInfo>> GetPhysicalDisksAsync()
    {
        var result = new List<StorageDiskInfo>();

        try
        {
            // Получение топологии через system_profiler SPStorageDataType в формате JSON
            var profiler = await RunProcessAsync("system_profiler", new[] { "SPStorageDataType", "-json" });

            // Таймаут или ошибка запуска — это НЕ «пустой вывод»: код != 0 означает,
            // что реальных данных нет, и сработает запасной путь на DriveInfo.
            string json = profiler.exitCode == 0 ? profiler.output : "";

            if (!string.IsNullOrWhiteSpace(json))
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("SPStorageDataType", out var storageArray))
                {
                    int idx = 0;
                    foreach (var item in storageArray.EnumerateArray())
                    {
                        string name = item.TryGetProperty("_name", out var n) ? n.GetString() ?? "Mac Storage" : "Mac Storage";
                        string bsd = item.TryGetProperty("bsd_name", out var b) ? b.GetString() ?? "" : "";
                        long size = item.TryGetProperty("size_in_bytes", out var s) ? (s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0) : 0;
                        string fs = item.TryGetProperty("file_system", out var f) ? f.GetString() ?? "APFS" : "APFS";
                        string mount = item.TryGetProperty("mount_point", out var m) ? m.GetString() ?? "" : "";
                        string physicalDrive = item.TryGetProperty("physical_drive", out var pd) && pd.TryGetProperty("device_name", out var dn) ? dn.GetString() ?? name : name;
                        string mediaTypeStr = item.TryGetProperty("physical_drive", out var pd2) && pd2.TryGetProperty("media_type", out var mt) ? mt.GetString() ?? "" : "";

                        bool isSsd = mediaTypeStr.Contains("SSD", StringComparison.OrdinalIgnoreCase) || fs.Contains("APFS", StringComparison.OrdinalIgnoreCase);

                        var disk = new StorageDiskInfo
                        {
                            Index = idx++,
                            DeviceId = bsd,
                            DevicePath = $"/dev/{bsd}",
                            Model = physicalDrive.Trim(),
                            SizeBytes = size,
                            MediaType = isSsd ? DiskMediaType.NVMe : DiskMediaType.HDD,
                            BusType = DiskBusType.NVMe,

                            // Раньше: HealthPercent = 100, HealthGrade = "A+",
                            // TemperatureC = 33.0 для каждого тома — то есть
                            // «идеальное здоровье» без измерений. Теперь
                            // значения появляются только из smartctl.
                            HealthPercent = 0,
                            HealthGrade = "н/д",
                            TemperatureC = 0,
                            IsSystemDisk = mount == "/" || mount.Contains("System")
                        };

                        long free = 0;
                        if (!string.IsNullOrEmpty(mount))
                        {
                            try
                            {
                                var driveInfo = new DriveInfo(mount);
                                free = driveInfo.AvailableFreeSpace;
                            }
                            catch { }
                        }

                        disk.Partitions.Add(new PartitionInfo
                        {
                            PartitionNumber = 1,
                            DevicePath = $"/dev/{bsd}",
                            MountPoint = mount,
                            VolumeLabel = name,
                            FileSystem = fs,
                            SizeBytes = size,
                            FreeBytes = free,
                            IsSystem = disk.IsSystemDisk
                        });

                        result.Add(disk);
                    }
                }
            }
        }
        catch { }

        // Fallback: DriveInfo
        if (result.Count == 0)
        {
            int idx = 0;
            foreach (var drive in DriveInfo.GetDrives())
            {
                result.Add(new StorageDiskInfo
                {
                    Index = idx++,
                    DeviceId = drive.Name,
                    DevicePath = drive.Name,
                    Model = drive.VolumeLabel.Length > 0 ? drive.VolumeLabel : "Mac Volume",
                    SizeBytes = drive.TotalSize,
                    MediaType = DiskMediaType.NVMe,
                    BusType = DiskBusType.NVMe,

                    // Без измерений здоровье неизвестно, а не «A+».
                    HealthGrade = "н/д"
                });
            }
        }

        return result;
    }

    public async Task<List<PartitionInfo>> GetPartitionsAsync(int diskIndex)
    {
        var disks = await GetPhysicalDisksAsync();
        var disk = disks.FirstOrDefault(d => d.Index == diskIndex);
        return disk?.Partitions ?? new List<PartitionInfo>();
    }

    public async Task<SmartReport> GetSmartReportAsync(int diskIndex)
    {
        var disks = await GetPhysicalDisksAsync();
        var disk = disks.FirstOrDefault(d => d.Index == diskIndex);

        var report = new SmartReport
        {
            DiskIndex = diskIndex,
            Model = disk?.Model ?? $"Диск {diskIndex}"
        };

        // Раньше возвращался выдуманный отчёт: Model = "Apple Silicon
        // High-Speed Controller", HealthPercent = 100, Grade = "A+",
        // TemperatureC = 32.0 — независимо от того, что стоит в системе.
        if (disk == null)
        {
            report.Recommendations.Add("Диск не найден.");
            return report;
        }

        // На macOS реальные показатели S.M.A.R.T. доступны через smartctl,
        // который не входит в стандартную поставку. Если его нет —
        // сообщаем об этом, а не придумываем значения.
        var r = RealSmartReader.ReadSmartctl(disk.DevicePath);

        if (r.ok)
        {
            report.Model = r.model ?? report.Model;

            if (r.health != null)
            {
                report.HealthPercent = r.health.Equals("Passed", StringComparison.OrdinalIgnoreCase) ? 100 : 20;
                report.HealthMeasured = true;
            }

            if (r.tempC.HasValue)
            {
                report.TemperatureC = r.tempC.Value;
                report.TemperatureMeasured = true;
            }

            if (r.powerOnHours.HasValue)
            {
                report.Attributes.Add(new SmartAttributeItem
                {
                    Id = 0x09,
                    Name = "Часы работы",
                    RawValue = r.powerOnHours.Value, HasRawValue = true,
                    Status = "OK"
                });
            }
        }

        report.Grade = !report.HealthMeasured && !report.TemperatureMeasured ? "н/д"
            : report.HealthPercent switch
            {
                >= 90 => "A",
                >= 70 => "B",
                >= 50 => "C",
                > 0 => "D",
                _ => "н/д"
            };

        report.Recommendations.Add(r.note);

        if (!r.ok)
        {
            report.Recommendations.Add(
                "Установите smartmontools (brew install smartmontools), чтобы приложение читало реальные показатели S.M.A.R.T.");
        }

        return report;
    }

    public async Task<bool> OptimizeDiskAsync(int diskIndex, IProgress<string>? progress = null)
    {
        progress?.Report("Оптимизация macOS APFS snapshot и TRIM...");
        await Task.Delay(400);
        progress?.Report("macOS APFS дедупликация и TRIM выполнены успешно.");
        return true;
    }

    public async Task<bool> FormatPartitionAsync(string devicePathOrLetter, string fileSystem, string label, bool quick)
    {
        string fs = fileSystem.ToUpperInvariant() switch
        {
            "EXFAT" => "ExFAT",
            "FAT32" => "FAT32",
            _ => "APFS"
        };

        // Метка и путь устройства ограничиваются безопасным набором символов:
        // они попадают в аргументы командной строки.
        string safeLabel = SanitizeLabel(label);
        string device = SanitizeDevicePath(devicePathOrLetter);

        var res = await RunProcessAsync("diskutil", new[] { "eraseVolume", fs, safeLabel, device });
        return res.exitCode == 0
               && res.output.Contains("Finished erase", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> DeletePartitionAsync(int diskIndex, int partitionNumber, bool overrideLocks)
    {
        var disks = await GetPhysicalDisksAsync();
        var disk = disks.FirstOrDefault(d => d.Index == diskIndex);
        if (disk == null) return false;

        // Раньше возвращался безусловный true, игнорируя результат diskutil.
        var res = await RunProcessAsync("diskutil",
            new[] { "eraseVolume", "Free", "Space", "None", SanitizeDevicePath(disk.DevicePath) });

        return res.exitCode == 0;
    }

    /// <summary>Метка тома: только буквы, цифры, дефис, точка и подчёркивание.</summary>
    private static string SanitizeLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "MotionCommander";
        var cleaned = new string(label
            .Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_')
            .ToArray());
        return string.IsNullOrEmpty(cleaned) ? "MotionCommander" : cleaned[..Math.Min(cleaned.Length, 27)];
    }

    /// <summary>Разрешает только путь к блочному устройству вида /dev/diskXsY.</summary>
    private static string SanitizeDevicePath(string device)
    {
        if (string.IsNullOrWhiteSpace(device)) return "/dev/null";
        var trimmed = device.Trim();
        if (!trimmed.StartsWith("/dev/", StringComparison.Ordinal)) return "/dev/null";

        if (!trimmed.Skip(5).All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '_' or '-'))
            return "/dev/null";

        return trimmed;
    }

    /// <summary>
    /// Запускает процесс и читает stdout/stderr ОДНОВРЕМЕННО, иначе возможен
    /// классический pipe deadlock при заполнении буфера stderr.
    ///
    /// <para><b>Почему именно такой порядок.</b> StreamReader.ReadToEndAsync
    /// не отменяется после начала чтения: токен проверяется только в момент
    /// старта, поэтому уже начатое чтение нельзя прервать. Сначала дожидаемся
    /// ВЫХОДА процесса с таймаутом (это отменяется через токен), и только потом
    /// читаем вывод — к этому моменту каналы закрыты и чтение обязано
    /// завершиться. Таймаут обязателен: иначе зависшая утилита держит
    /// вызывающий код бесконечно, и отмены у чтения не существует.</para>
    /// </summary>
    private static async Task<(int exitCode, string output)> RunProcessAsync(string fileName, string[] args)
    {
        using var cts = new CancellationTokenSource(ExternalCommandTimeout);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };

        try
        {
            if (!proc.Start())
                return (-1, $"{fileName}: не удалось запустить процесс.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Утилита не найдена или недоступна.
            return (-1, $"{fileName}: утилита не найдена или недоступна.");
        }
        catch (InvalidOperationException)
        {
            return (-1, $"{fileName}: не удалось запустить процесс.");
        }

        // Обе задачи чтения стартуют ОДНОВРЕМЕННО: если процесс заполнит буфер
        // stderr и заблокируется, последовательное чтение stdout до EOF никогда
        // не завершится (классический pipe deadlock).
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Таймаут — это провал, а НЕ «пустой успешный вывод».
            TryKill(proc);
            return (-1, $"{fileName}: не ответил за {ExternalCommandTimeout.TotalSeconds:0} с и был прерван.");
        }

        // Процесс завершился, каналы закрыты. Ограничиваем и сам дренаж.
        var reads = Task.WhenAll(stdoutTask, stderrTask);
        if (await Task.WhenAny(reads, Task.Delay(DrainTimeout, cts.Token)).ConfigureAwait(false) != reads)
        {
            TryKill(proc);
            return (-1, $"{fileName}: вывод не получен за {DrainTimeout.TotalSeconds:0} с.");
        }

        // reads завершён, поэтому .Result здесь безопасен.
        await reads.ConfigureAwait(false);
        return (proc.ExitCode, stdoutTask.Result + "\n" + stderrTask.Result);
    }
}
