using System.Diagnostics;
using System.IO;
using System.Text.Json;
using MotionCommander.Core.Models;

namespace MotionCommander.Core.Storage.Linux;

public sealed class LinuxStorageProvider : IStorageProvider
{
    /// <summary>
    /// Максимальное ожидание внешней утилиты (lsblk, mkfs.*, parted).
    /// Раньше ожидание не было ограничено: зависший parted или mkfs держали
    /// вызывающий код бесконечно, и отмены у него не было вовсе.
    /// </summary>
    private static readonly TimeSpan ExternalCommandTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// fstrim -av обходит все смонтированные ФС, поэтому на больших массивах
    /// он штатно работает дольше обычного: для него отдельный лимит.
    /// </summary>
    private static readonly TimeSpan FstrimTimeout = TimeSpan.FromMinutes(5);

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
            // 1. Попытка использования lsblk в JSON-формате
            var lsblk = await RunProcessAsync("lsblk", new[]
            { "-J", "-b", "-o", "NAME,PATH,SIZE,ROTA,TYPE,MOUNTPOINT,FSTYPE,LABEL,MODEL,TRAN" });

            // Таймаут или ошибка запуска — это НЕ «пустой вывод»: при коде != 0
            // реальных данных нет, и ниже сработает запасной путь через /sys/block.
            string lsblkOutput = lsblk.exitCode == 0 ? lsblk.output : "";

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

        var res = await RunProcessAsync("fstrim", new[] { "-av" }, FstrimTimeout);

        // Раньше здесь возврашался безусловный true: зависший или упавший fstrim
        // показывался как успешная оптимизация. Таймаут — это тоже провал.
        if (res.exitCode != 0)
        {
            string reason = res.output.Trim();
            if (reason.Length > 300) reason = reason[..300] + "...";
            progress?.Report(reason.Length > 0
                ? $"fstrim не выполнил TRIM (код {res.exitCode}): {reason}"
                : $"fstrim не выполнил TRIM (код {res.exitCode}).");
            return false;
        }

        progress?.Report(string.IsNullOrWhiteSpace(res.output)
            ? "Оптимизация Trim завершена успешно."
            : res.output.Trim());
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

    /// <summary>
    /// Запускает процесс и читает stdout/stderr ОДНОВРЕМЕННО, иначе возможен
    /// классический pipe deadlock при заполнении буфера stderr.
    ///
    /// <para><b>Почему именно такой порядок.</b> StreamReader.ReadToEndAsync
    /// не отменяется после начала чтения: токен проверяется только в момент
    /// старта, поэтому уже начатое чтение нельзя прервать. Сначала дожидаемся
    /// ВЫХОДА процесса с таймаутом (это отменяется через токен), и только потом
    /// читаем вывод — к этому моменту каналы закрыты и чтение обязано
    /// завершиться. Таймаут обязателен: без него зависшая утилита держит
    /// вызывающий код вечно, и отмены у чтения не существует.</para>
    /// </summary>
    private static async Task<(int exitCode, string output)> RunProcessAsync(
        string fileName, string[] args, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? ExternalCommandTimeout);

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

        // Обе задачи чтения стартуют ОДНОВРЕМЕННО. Иначе, если процесс
        // заполнит буфер stderr и заблокируется, чтение stdout до EOF
        // никогда не завершится (классический pipe deadlock).
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
            return (-1, $"{fileName}: не ответил за {(timeout ?? ExternalCommandTimeout).TotalSeconds:0} с и был прерван.");
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
