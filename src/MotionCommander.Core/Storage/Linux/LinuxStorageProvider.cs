using System.Diagnostics;
using System.IO;
using System.Text.Json;
using MotionCommander.Core.Models;

namespace MotionCommander.Core.Storage.Linux;

public sealed class LinuxStorageProvider : IStorageProvider
{
    public async Task<List<StorageDiskInfo>> GetPhysicalDisksAsync()
    {
        var result = new List<StorageDiskInfo>();

        try
        {
            // 1. Попытка использования lsblk в JSON-формате
            string lsblkOutput = (await RunProcessAsync("lsblk", new[]
            { "-J", "-b", "-o", "NAME,PATH,SIZE,ROTA,TYPE,MOUNTPOINT,FSTYPE,LABEL,MODEL,TRAN" })).output;
            if (!string.IsNullOrWhiteSpace(lsblkOutput))
            {
                using var doc = JsonDocument.Parse(lsblkOutput);
                if (doc.RootElement.TryGetProperty("blockdevices", out var blockdevices))
                {
                    int diskIdx = 0;
                    foreach (var dev in blockdevices.EnumerateArray())
                    {
                        string type = dev.TryGetProperty("type", out var tProp) ? tProp.GetString() ?? "" : "";
                        if (type != "disk") continue;

                        string name = dev.TryGetProperty("name", out var nProp) ? nProp.GetString() ?? "" : "";
                        string path = dev.TryGetProperty("path", out var pProp) ? pProp.GetString() ?? $"/dev/{name}" : $"/dev/{name}";
                        string model = dev.TryGetProperty("model", out var mProp) ? mProp.GetString() ?? name : name;
                        string tran = dev.TryGetProperty("tran", out var trProp) ? trProp.GetString() ?? "" : "";
                        long size = dev.TryGetProperty("size", out var sProp) ? (sProp.ValueKind == JsonValueKind.Number ? sProp.GetInt64() : 0) : 0;
                        int rota = dev.TryGetProperty("rota", out var rProp) ? (rProp.ValueKind == JsonValueKind.Number ? rProp.GetInt32() : 0) : 0;

                        var disk = new StorageDiskInfo
                        {
                            Index = diskIdx++,
                            DeviceId = name,
                            DevicePath = path,
                            Model = string.IsNullOrWhiteSpace(model) ? $"Disk {name}" : model.Trim(),
                            SizeBytes = size,
                            MediaType = rota == 0 ? (tran.Equals("nvme", StringComparison.OrdinalIgnoreCase) || name.StartsWith("nvme") ? DiskMediaType.NVMe : DiskMediaType.SSD) : DiskMediaType.HDD,
                            BusType = tran.ToLowerInvariant() switch
                            {
                                "nvme" => DiskBusType.NVMe,
                                "sata" => DiskBusType.SATA,
                                "usb" => DiskBusType.USB,
                                "scsi" => DiskBusType.SCSI,
                                _ => name.StartsWith("nvme") ? DiskBusType.NVMe : DiskBusType.SATA
                            },
                            // Раньше здесь стояло HealthPercent = 100, HealthGrade = "A+"
                            // и TemperatureC = 34.0 для КАЖДОГО диска, что выдавало
                            // «идеальное здоровье» без единого измерения.
                            // Теперь эти поля остаются неизмеренными и заполняются
                            // реальными данными только в GetSmartReportAsync.
                            HealthPercent = 0,
                            HealthGrade = "н/д",
                            TemperatureC = 0
                        };

                        // Разделы
                        if (dev.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
                        {
                            int pNum = 1;
                            foreach (var child in children.EnumerateArray())
                            {
                                string cName = child.TryGetProperty("name", out var cn) ? cn.GetString() ?? "" : "";
                                string cPath = child.TryGetProperty("path", out var cp) ? cp.GetString() ?? $"/dev/{cName}" : $"/dev/{cName}";
                                long cSize = child.TryGetProperty("size", out var cs) ? (cs.ValueKind == JsonValueKind.Number ? cs.GetInt64() : 0) : 0;
                                string fstype = child.TryGetProperty("fstype", out var cfs) ? cfs.GetString() ?? "Unknown" : "Unknown";
                                string mount = child.TryGetProperty("mountpoint", out var cmp) ? cmp.GetString() ?? "" : "";
                                string label = child.TryGetProperty("label", out var cl) ? cl.GetString() ?? "" : "";

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
                                    PartitionNumber = pNum++,
                                    DevicePath = cPath,
                                    MountPoint = mount,
                                    VolumeLabel = label,
                                    FileSystem = fstype,
                                    SizeBytes = cSize,
                                    FreeBytes = free,
                                    IsSystem = mount == "/" || mount == "/boot"
                                });
                            }
                        }

                        result.Add(disk);
                    }
                }
            }
        }
        catch { }

        // Fallback: прямое чтение /sys/block если lsblk недоступен
        if (result.Count == 0 && Directory.Exists("/sys/block"))
        {
            int idx = 0;
            foreach (var dir in Directory.GetDirectories("/sys/block"))
            {
                string name = Path.GetFileName(dir);
                if (name.StartsWith("loop") || name.StartsWith("ram")) continue;

                long sizeBytes = 0;
                string sizePath = Path.Combine(dir, "size");
                if (File.Exists(sizePath) && long.TryParse(File.ReadAllText(sizePath).Trim(), out var sectors))
                {
                    sizeBytes = sectors * 512;
                }

                int rota = 0;
                string rotaPath = Path.Combine(dir, "queue", "rotational");
                if (File.Exists(rotaPath)) int.TryParse(File.ReadAllText(rotaPath).Trim(), out rota);

                result.Add(new StorageDiskInfo
                {
                    Index = idx++,
                    DeviceId = name,
                    DevicePath = $"/dev/{name}",
                    Model = name.ToUpperInvariant(),
                    SizeBytes = sizeBytes,
                    MediaType = rota == 0 ? (name.StartsWith("nvme") ? DiskMediaType.NVMe : DiskMediaType.SSD) : DiskMediaType.HDD,
                    BusType = name.StartsWith("nvme") ? DiskBusType.NVMe : DiskBusType.SATA
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

        // Раньше здесь стояли жёсткие HealthPercent = 98, Grade = "A+" и
        // TemperatureC = 36.0 ДО попытки чтения smartctl, поэтому даже при
        // полном отсутствии данных пользователь получал «уверенный» отчёт.
        // Теперь показатели появляются только из реального опроса контроллера.
        if (disk == null)
        {
            report.Recommendations.Add("Диск не найден.");
            return report;
        }

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

            if (r.wearPercent.HasValue)
            {
                report.Attributes.Add(new SmartAttributeItem
                {
                    Id = 0xE7,
                    Name = "Износ",
                    RawValue = (long)r.wearPercent.Value,
                    HasRawValue = true,
                    Status = r.wearPercent.Value > 90 ? "Warning" : "OK"
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

        // Проверка TRIM — реальная, а не декларация.
        try
        {
            var fstrim = await RunProcessAsync("fstrim", new[] { "--help" });
            if (fstrim.exitCode == 0)
            {
                report.Recommendations.Add("Утилита fstrim доступна в системе.");
            }
        }
        catch { }

        return report;
    }

    public async Task<bool> OptimizeDiskAsync(int diskIndex, IProgress<string>? progress = null)
    {
        progress?.Report("Выполнение fstrim для сброса свободных блоков на Linux...");
        string output = (await RunProcessAsync("fstrim", new[] { "-av" })).output;
        progress?.Report(string.IsNullOrWhiteSpace(output) ? "Оптимизация Trim завершена успешно." : output.Trim());
        return true;
    }

    public async Task<bool> FormatPartitionAsync(string devicePathOrLetter, string fileSystem, string label, bool quick)
    {
        // Метка тома ограничивается безопасным набором символов: она попадает
        // в аргументы командной строки, где кавычки и «;» опасны.
        string safeLabel = SanitizeLabel(label);
        string device = SanitizeDevicePath(devicePathOrLetter);

        var (exe, args) = fileSystem.ToLowerInvariant() switch
        {
            "btrfs" => ("mkfs.btrfs", new[] { "-f", "-L", safeLabel, device }),
            "fat32" or "vfat" => ("mkfs.vfat", new[] { "-F", "32", "-n", safeLabel, device }),
            "ntfs" => ("mkfs.ntfs", new[] { "-f", "-L", safeLabel, device }),
            _ => ("mkfs.ext4", new[] { "-F", "-L", safeLabel, device })
        };

        // ArgumentList вместо строковой склейки — защита от command injection.
        var res = await RunProcessAsync(exe, args);
        return res.exitCode == 0;
    }

    public async Task<bool> DeletePartitionAsync(int diskIndex, int partitionNumber, bool overrideLocks)
    {
        var disks = await GetPhysicalDisksAsync();
        var disk = disks.FirstOrDefault(d => d.Index == diskIndex);
        if (disk == null) return false;
        if (partitionNumber < 0) return false;

        // Раньше возвращался безусловный true, игнорируя результат parted.
        var res = await RunProcessAsync("parted", new[] { "-s", disk.DevicePath, "rm", partitionNumber.ToString() });
        return res.exitCode == 0;
    }

    /// <summary>Допускает только буквы, цифры, дефис и подчёркивание (лимит метки ext4 — 16 символов).</summary>
    private static string SanitizeLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "DATA";
        var cleaned = new string(label
            .Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            .ToArray());
        return string.IsNullOrEmpty(cleaned) ? "DATA" : cleaned[..Math.Min(cleaned.Length, 16)];
    }

    /// <summary>Разрешает только путь к блочному устройству вида /dev/xxx.</summary>
    private static string SanitizeDevicePath(string device)
    {
        if (string.IsNullOrWhiteSpace(device)) return "/dev/null";
        var trimmed = device.Trim();
        if (!trimmed.StartsWith("/dev/", StringComparison.Ordinal)) return "/dev/null";

        // Только буквы, цифры и разделители пути.
        if (!trimmed.Skip(5).All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '_' or '-'))
            return "/dev/null";

        return trimmed;
    }

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

            // Обе задачи чтения стартуют ОДНОВРЕМЕННО. Иначе, если процесс
            // заполнит буфер stderr и заблокируется, чтение stdout до EOF
            // никогда не завершится (классический pipe deadlock).
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
