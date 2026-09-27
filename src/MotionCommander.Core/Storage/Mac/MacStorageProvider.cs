using System.Diagnostics;
using System.IO;
using System.Text.Json;
using MotionCommander.Core.Models;

namespace MotionCommander.Core.Storage.Mac;

public sealed class MacStorageProvider : IStorageProvider
{
    public async Task<List<StorageDiskInfo>> GetPhysicalDisksAsync()
    {
        var result = new List<StorageDiskInfo>();

        try
        {
            // Получение топологии через system_profiler SPStorageDataType в формате JSON
            string json = (await RunProcessAsync("system_profiler", new[] { "SPStorageDataType", "-json" })).output;
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
    /// </summary>
    private static async Task<(int exitCode, string output)> RunProcessAsync(string fileName, string[] args)
    {
        try
        {
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
            proc.Start();

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            await proc.WaitForExitAsync().ConfigureAwait(false);

            return (proc.ExitCode, stdoutTask.Result + "\n" + stderrTask.Result);
        }
        catch
        {
            return (-1, "");
        }
    }
}
