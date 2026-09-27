using System.Diagnostics;
using System.IO;
using MotionCommander.Core.Models;

namespace MotionCommander.Core.Storage.Windows;

public sealed class WindowsStorageProvider : IStorageProvider
{
    /// <summary>
    /// Максимальное ожидание внешней утилиты по умолчанию.
    /// Раньше ожидание не было ничем ограничено: зависший diskpart, ожидающий
    /// ввода, или «залипший» defrag навсегда блокировали вызывающий код.
    /// </summary>
    private static readonly TimeSpan ExternalCommandTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// defrag.exe /O — это штатно долгая оптимизация тома (дефрагментация
    /// HDD на терабайты занимает десятки минут), поэтому у неё свой лимит.
    /// </summary>
    private static readonly TimeSpan DefragTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Скрипты diskpart (format/delete) тоже могут идти долго, особенно
    /// форматирование больших томов без ключа quick.
    /// </summary>
    private static readonly TimeSpan DiskpartTimeout = TimeSpan.FromMinutes(10);

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

    /// <summary>
    /// Возвращает реальные физические накопители.
    ///
    /// <para><b>Что было не так.</b> Метод перечислял только логические тома
    /// через DriveInfo и затем ПОДСТАВЛЯЛ показатели по формуле
    /// «если это диск C:, значит NVMe 38 °C, иначе SSD 32 °C», а здоровье
    /// и оценку писал константами 100 и «A+». Ни один из этих параметров
    /// не имел отношения к реальному оборудованию.</para>
    ///
    /// <para>Теперь используется WMI (MSFT_Disk / Win32_DiskDrive) для
    /// реальных модели, серийного номера, размера, типа носителя и состояния.
    /// Показатели, которые контроллер не отдаёт, помечаются как неизмеренные,
    /// и в вывод попадают как «нет данных», а не как выдуманные числа.</para>
    /// </summary>
    public Task<List<StorageDiskInfo>> GetPhysicalDisksAsync()
    {
        var result = new List<StorageDiskInfo>();

        if (!OperatingSystem.IsWindows())
            return Task.FromResult(result);

        // ── 1. Реальные физические диски ─────────────────────────────────
        try
        {
            var scope = new System.Management.ManagementScope(@"\\.\root\microsoft\windows\storage");
            scope.Options.Timeout = TimeSpan.FromSeconds(5);

            using var searcher = new System.Management.ManagementObjectSearcher(scope,
                new System.Management.ObjectQuery(
                    "SELECT Number, FriendlyName, SerialNumber, BusType, Size, HealthStatus, PartitionStyle FROM MSFT_Disk"));

            using var results = searcher.Get();
            foreach (System.Management.ManagementObject obj in results)
            {
                using (obj)
                {
                    int number = Convert.ToInt32(obj["Number"] ?? -1);
                    if (number < 0) continue;

                    long size = Convert.ToInt64(obj["Size"] ?? 0);
                    int busCode = Convert.ToInt32(obj["BusType"] ?? 0);
                    ushort healthCode = 0;
                    try { healthCode = Convert.ToUInt16(obj["HealthStatus"] ?? 0); } catch { }

                    var disk = new StorageDiskInfo
                    {
                        Index = number,
                        DeviceId = number.ToString(),
                        DevicePath = $@"\\.\PHYSICALDRIVE{number}",
                        Model = obj["FriendlyName"]?.ToString() ?? $"Диск {number}",
                        SerialNumber = (obj["SerialNumber"]?.ToString() ?? "").Trim(),
                        SizeBytes = size,
                        BusType = MapBusType(busCode),
                        MediaType = MapMediaType(busCode),
                        IsSystemDisk = number == 0
                    };

                    // Реальное состояние из MSFT_Disk.HealthStatus (UInt16-энум).
                    // 0 = Unknown — состояние НЕИЗВЕСТНО, а не «здоров».
                    disk.HealthMeasured = healthCode is 1 or 2 or 3;
                    disk.HealthPercent = healthCode switch
                    {
                        1 => 100,
                        2 => 60,
                        3 => 10,
                        _ => 0
                    };
                    disk.HealthGrade = !disk.HealthMeasured ? "н/д"
                        : disk.HealthPercent switch
                        {
                            >= 90 => "A",
                            >= 70 => "B",
                            >= 50 => "C",
                            _ => "D"
                        };

                    // Температура и износ заполняются отдельно, реальными
                    // счётчиками. Здесь они остаются неизмеренными.
                    result.Add(disk);
                }
            }
        }
        catch (System.Management.ManagementException)
        {
            // WMI-хранилище недоступно — переходим к Win32_DiskDrive.
            result.Clear();
        }

        // ── 2. Запасной путь: Win32_DiskDrive ───────────────────────────
        if (result.Count == 0)
        {
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT Index, Model, SerialNumber, InterfaceType, Size, MediaType, Status FROM Win32_DiskDrive");
                using var results = searcher.Get();

                foreach (System.Management.ManagementObject obj in results)
                {
                    using (obj)
                    {
                        int index = Convert.ToInt32(obj["Index"] ?? 0);
                        string ifType = obj["InterfaceType"]?.ToString() ?? "";
                        int mediaType = Convert.ToInt32(obj["MediaType"] ?? 0);
                        string status = obj["Status"]?.ToString()?.Trim() ?? "";

                        DiskBusType bus = ifType.Equals("USB", StringComparison.OrdinalIgnoreCase)
                            ? DiskBusType.USB
                            : DiskBusType.Unknown;

                        result.Add(new StorageDiskInfo
                        {
                            Index = index,
                            DeviceId = index.ToString(),
                            DevicePath = $@"\\.\PHYSICALDRIVE{index}",
                            Model = obj["Model"]?.ToString() ?? $"Диск {index}",
                            SerialNumber = (obj["SerialNumber"]?.ToString() ?? "").Trim(),
                            SizeBytes = Convert.ToInt64(obj["Size"] ?? 0),
                            BusType = bus,
                            MediaType = mediaType == 3
                                ? DiskMediaType.HDD
                                : bus == DiskBusType.USB ? DiskMediaType.FlashMemory
                                : DiskMediaType.Unknown,
                            // Win32_DiskDrive.Status — реальная строка состояния.
                            HealthMeasured = status.Length > 0,
                            HealthPercent = status.ToLowerInvariant() switch
                            {
                                "ok" => 100,
                                "degraded" => 60,
                                _ => status.Length > 0 ? 20 : 0
                            },
                            HealthGrade = status.Length == 0 ? "н/д"
                                : status.Equals("OK", StringComparison.OrdinalIgnoreCase) ? "A" : "D",
                            IsSystemDisk = index == 0
                        });
                    }
                }
            }
            catch { }
        }

        // ── 3. Дополняем реальными разделами и свободным местом ─────────
        // Используем MSFT_Partition + MSFT_Volume из хранилища Windows.
        // Раньше здесь использовался Win32_LogicalDiskToPartition, у которого
        // нет свойства DiskNumber (это ассоциативный класс со ссылками
        // Antecedent/Dependent), из-за чего разделы не выводились вовсе.
        try
        {
            var storageScope = new System.Management.ManagementScope(@"\\.\root\microsoft\windows\storage");
            storageScope.Options.Timeout = TimeSpan.FromSeconds(5);

            // Собираем реальные данные томов: буква -> (метка, ФС, размер, свободно)
            var volumes = new Dictionary<string, (string label, string fs, long size, long free)>(
                StringComparer.OrdinalIgnoreCase);

            using (var vSearcher = new System.Management.ManagementObjectSearcher(storageScope,
                new System.Management.ObjectQuery(
                    "SELECT DriveLetter, FileSystemLabel, FileSystem, Size, SizeRemaining FROM MSFT_Volume")))
            {
                using var vResults = vSearcher.Get();
                foreach (System.Management.ManagementObject v in vResults)
                {
                    using (v)
                    {
                        string letter = v["DriveLetter"]?.ToString() ?? "";
                        if (string.IsNullOrWhiteSpace(letter)) continue;

                        volumes[letter] = (
                            v["FileSystemLabel"]?.ToString() ?? "",
                            v["FileSystem"]?.ToString() ?? "Неизвестно",
                            Convert.ToInt64(v["Size"] ?? 0),
                            Convert.ToInt64(v["SizeRemaining"] ?? 0));
                    }
                }
            }

            using (var pSearcher = new System.Management.ManagementObjectSearcher(storageScope,
                new System.Management.ObjectQuery(
                    "SELECT DiskNumber, PartitionNumber, DriveLetter, Size, IsBoot, IsSystem FROM MSFT_Partition")))
            {
                using var pResults = pSearcher.Get();
                foreach (System.Management.ManagementObject p in pResults)
                {
                    using (p)
                    {
                        // Обработка ПОСТРОЧНАЯ: одно некорректно прочитанное
                        // свойство не должно прерывать вывод остальных разделов.
                        try
                        {
                            int diskNum = Convert.ToInt32(p["DiskNumber"] ?? -1);
                            var target = result.FirstOrDefault(d => d.Index == diskNum);
                            if (target == null) continue;

                            // ВАЖНО: у MSFT_Partition свойство DriveLetter имеет
                            // тип char, и для системных разделов (EFI, MSR)
                            // содержит NUL (код 0), а не пустую строку.
                            // String.Trim() NUL не удаляет, поэтому убираем
                            // все непечатаемые символы явно.
                            string letter = CleanDriveLetter(p["DriveLetter"]);
                            long size = Convert.ToInt64(p["Size"] ?? 0);

                            bool isBoot = false, isSystem = false;
                            try { isBoot = Convert.ToBoolean(p["IsBoot"] ?? false); } catch { }
                            try { isSystem = Convert.ToBoolean(p["IsSystem"] ?? false); } catch { }

                            long free = 0;
                            string label = "";
                            string fs = "Без тома";

                            if (letter.Length > 0 && volumes.TryGetValue(letter, out var vol))
                            {
                                free = vol.free;
                                label = vol.label;
                                fs = vol.fs;
                                if (vol.size > 0) size = vol.size;
                            }

                            target.Partitions.Add(new PartitionInfo
                            {
                                PartitionNumber = Convert.ToInt32(p["PartitionNumber"] ?? 0),
                                DevicePath = $@"\\.\PHYSICALDRIVE{diskNum}",
                                MountPoint = letter,
                                VolumeLabel = label,
                                FileSystem = fs,
                                SizeBytes = size,
                                FreeBytes = free,
                                IsBoot = isBoot,
                                IsSystem = isSystem
                            });
                        }
                        catch
                        {
                            // Пропускаем только этот раздел.
                        }
                    }
                }
            }
        }
        catch { }

        // ── 4. Уточняем тип носителя по признаку шпинделя ────────────────
        // MSFT_Disk не различает HDD и SATA SSD. Различает MSFT_PhysicalDisk:
        // SpindleSpeed > 0 — вращающийся диск, 0 — твердотельный.
        try
        {
            var physScope = new System.Management.ManagementScope(@"\\.\root\microsoft\windows\storage");
            physScope.Options.Timeout = TimeSpan.FromSeconds(5);

            using var searcher = new System.Management.ManagementObjectSearcher(physScope,
                new System.Management.ObjectQuery("SELECT DeviceId, MediaType, SpindleSpeed FROM MSFT_PhysicalDisk"));
            using var results = searcher.Get();

            foreach (System.Management.ManagementObject obj in results)
            {
                using (obj)
                {
                    if (!int.TryParse(obj["DeviceId"]?.ToString(), out int devId)) continue;
                    var target = result.FirstOrDefault(d => d.Index == devId);
                    if (target == null) continue;

                    int spindle = 0, mediaType = 0;
                    try { spindle = Convert.ToInt32(obj["SpindleSpeed"] ?? 0); } catch { }
                    try { mediaType = Convert.ToInt32(obj["MediaType"] ?? 0); } catch { }

                    if (spindle > 0 || mediaType == 3)
                    {
                        // Реальный признак вращающегося диска.
                        target.MediaType = DiskMediaType.HDD;
                    }
                    else if (target.MediaType == DiskMediaType.Unknown && mediaType == 4)
                    {
                        target.MediaType = DiskMediaType.SSD;
                    }
                }
            }
        }
        catch { }

        // ── 5. Дополняем реальными счётчиками надёжности ────────────────
        foreach (var disk in result)
        {
            var r = RealSmartReader.Read(disk.Index);
            if (!r.ok) continue;

            if (r.tempC.HasValue)
            {
                disk.TemperatureC = r.tempC.Value;
                disk.TemperatureMeasured = true;
            }

            if (r.wearPercent.HasValue)
            {
                disk.WearLevelPercent = r.wearPercent.Value;
                disk.WearMeasured = true;
            }

            if (r.powerOnHours.HasValue) disk.PowerOnHours = r.powerOnHours.Value;
            if (r.powerCycles.HasValue) disk.PowerCycles = r.powerCycles.Value;
        }

        return Task.FromResult(result);
    }

    /// <summary>
    /// Нормализует букву тома.
    ///
    /// У MSFT_Partition свойство DriveLetter имеет тип <c>char</c>, и для
    /// системных разделов (EFI, MSR, recovery) содержит NUL (код 0), а не
    /// пустую строку. <c>String.Trim()</c> символ NUL не удаляет, поэтому
    /// разделы без буквы приходилось выводить как « :\». Здесь явно
    /// отбрасываются все непечатаемые символы.
    /// </summary>
    private static string CleanDriveLetter(object? raw)
    {
        string s = raw?.ToString() ?? "";

        foreach (char c in s)
        {
            if (char.IsControl(c)) continue;
            return char.IsLetter(c) ? c.ToString().ToUpperInvariant() : "";
        }

        return "";
    }

    private static DiskBusType MapBusType(int code) => code switch
    {
        17 => DiskBusType.NVMe,
        11 => DiskBusType.SATA,
        7 => DiskBusType.USB,
        10 => DiskBusType.SAS,
        14 => DiskBusType.Virtual,
        _ => DiskBusType.Unknown
    };

    /// <summary>
    /// Тип носителя по коду шины. Раньше тип определялся так:
    /// «если том C:, значит NVMe, иначе SSD» — то есть по букве диска.
    /// Теперь используется реальный код шины, а не догадка.
    /// </summary>
    private static DiskMediaType MapMediaType(int busCode) => busCode switch
    {
        17 => DiskMediaType.NVMe,
        7 => DiskMediaType.FlashMemory,
        _ => DiskMediaType.Unknown
    };

    public async Task<List<PartitionInfo>> GetPartitionsAsync(int diskIndex)
    {
        var disks = await GetPhysicalDisksAsync();
        var disk = disks.FirstOrDefault(d => d.Index == diskIndex);
        return disk?.Partitions ?? new List<PartitionInfo>();
    }

    public Task<SmartReport> GetSmartReportAsync(int diskIndex)
    {
        var report = new SmartReport
        {
            DiskIndex = diskIndex
        };

        // Раньше здесь возвращался полностью выдуманный отчёт:
        // Model = "Windows Certified Storage Controller", HealthPercent = 100,
        // Grade = "A+", TemperatureC = 36.0 — причём аргумент diskIndex
        // вообще не использовался, то есть результат был одинаковым для
        // любого диска. Теперь читаем реальные счётчики надёжности.
        if (!OperatingSystem.IsWindows())
        {
            report.Model = "—";
            report.Recommendations.Add("WMI доступен только в Windows.");
            return Task.FromResult(report);
        }

        var r = RealSmartReader.Read(diskIndex);

        report.Model = r.model ?? $"Диск {diskIndex}";

        if (r.health != null)
        {
            // Оценка выводится из РЕАЛЬНОГО состояния, сообщённого Windows.
            report.HealthPercent = r.health.ToLowerInvariant() switch
            {
                "healthy" or "ok" or "good" => 100,
                "warning" or "caution" or "degraded" => 60,
                "unhealthy" or "failed" or "critical" => 10,
                _ => 50
            };
        }
        else
        {
            // Нет данных — не выдаём 100 по умолчанию.
            report.HealthPercent = 0;
        }

        report.HealthMeasured = r.health != null;
        report.TemperatureMeasured = r.tempC.HasValue;
        report.TemperatureC = r.tempC ?? 0;

        if (r.tempC.HasValue)
        {
            var attrs = new List<SmartAttributeItem>
            {
                new()
                {
                    Id = 0xC2,
                    Name = "Температура",
                    CurrentValue = "—",
                    WorstValue = "—",
                    Threshold = "—",
                    RawValue = (long)r.tempC.Value,
                    HasRawValue = true,
                    Status = "OK"
                }
            };

            if (r.powerOnHours.HasValue)
            {
                attrs.Add(new SmartAttributeItem
                {
                    Id = 0x09,
                    Name = "Часы работы",
                    RawValue = r.powerOnHours.Value, HasRawValue = true,
                    Status = "OK"
                });
            }

            if (r.powerCycles.HasValue)
            {
                attrs.Add(new SmartAttributeItem
                {
                    Id = 0x0C,
                    Name = "Циклы включения",
                    RawValue = r.powerCycles.Value, HasRawValue = true,
                    Status = "OK"
                });
            }

            if (r.wearPercent.HasValue)
            {
                attrs.Add(new SmartAttributeItem
                {
                    Id = 0xE7,
                    Name = "Износ",
                    RawValue = (long)r.wearPercent.Value,
                    HasRawValue = true,
                    Status = r.wearPercent.Value > 90 ? "Warning" : "OK"
                });
            }

            if (r.readErrors.HasValue || r.writeErrors.HasValue)
            {
                attrs.Add(new SmartAttributeItem
                {
                    Id = 0xF1,
                    Name = $"Ошибки чтения/записи: {r.readErrors.GetValueOrDefault()}/{r.writeErrors.GetValueOrDefault()}",
                    RawValue = r.readErrors.GetValueOrDefault() + r.writeErrors.GetValueOrDefault(),
                    HasRawValue = true,
                    Status = (r.readErrors.GetValueOrDefault() + r.writeErrors.GetValueOrDefault()) > 0 ? "Warning" : "OK"
                });
            }

            report.Attributes = attrs;
        }

        report.Grade = r.health == null && !r.tempC.HasValue && !r.wearPercent.HasValue
            ? "н/д"
            : report.HealthPercent switch
            {
                >= 90 => "A",
                >= 70 => "B",
                >= 50 => "C",
                > 0 => "D",
                _ => "н/д"
            };

        report.Recommendations.Add(r.note);
        if (r.source != null) report.Recommendations.Add($"Источник данных: {r.source}.");

        return Task.FromResult(report);
    }

    public async Task<bool> OptimizeDiskAsync(int diskIndex, IProgress<string>? progress = null)
    {
        progress?.Report("Вызов Windows Optimize-Volume (ReTrim / Defrag)...");
        var disks = await GetPhysicalDisksAsync();
        var disk = disks.FirstOrDefault(d => d.Index == diskIndex);
        string driveLetter = disk?.DeviceId.TrimEnd(':') ?? "C";

        // ArgumentList вместо строковой интерполяции (защита от инъекции).
        var psi = new ProcessStartInfo
        {
            FileName = "defrag.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add($"{driveLetter}:");
        psi.ArgumentList.Add("/O");
        psi.ArgumentList.Add("/U");

        return await RunAndReportAsync(psi, progress, DefragTimeout) == 0;
    }

    public async Task<bool> FormatPartitionAsync(string devicePathOrLetter, string fileSystem, string label, bool quick)
    {
        string letter = SanitizeVolumeLetter(devicePathOrLetter);
        string fs = SanitizeIdentifier(fileSystem, "NTFS");
        string safeLabel = SanitizeIdentifier(label, "Label");

        string script = $"select volume {letter}\nformat fs={fs} label=\"{safeLabel}\" {(quick ? "quick" : "")}\nexit\n";
        string scriptFile = Path.Combine(Path.GetTempPath(), $"format_{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(scriptFile, script);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "diskpart.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("/s");
            psi.ArgumentList.Add(scriptFile);

            // Раньше результат игнорировался и всегда возвращался true —
            // вызывающий код показывал «✔ Успешно» даже при упавшем diskpart.
            return await RunAndReportAsync(psi, null, DiskpartTimeout) == 0;
        }
        finally
        {
            try { File.Delete(scriptFile); } catch { /* файл может быть занят diskpart */ }
        }
    }

    public async Task<bool> DeletePartitionAsync(int diskIndex, int partitionNumber, bool overrideLocks)
    {
        // Числовые параметры валидируем: иначе возможна инъекция в скрипт diskpart.
        if (diskIndex < 0 || partitionNumber < 0)
            return false;

        string script = $"select disk {diskIndex}\nselect partition {partitionNumber}\ndelete partition {(overrideLocks ? "override" : "")}\nexit\n";
        string scriptFile = Path.Combine(Path.GetTempPath(), $"del_{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(scriptFile, script);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "diskpart.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("/s");
            psi.ArgumentList.Add(scriptFile);

            return await RunAndReportAsync(psi, null, DiskpartTimeout) == 0;
        }
        finally
        {
            try { File.Delete(scriptFile); } catch { }
        }
    }

    /// <summary>Оставляет только букву тома — защита от инъекции в скрипт diskpart.</summary>
    private static string SanitizeVolumeLetter(string input)
    {
        string s = (input ?? "").Trim().TrimEnd(':', '\\', '/');
        return s.Length == 1 && char.IsLetter(s[0]) ? s.ToUpperInvariant() : "C";
    }

    /// <summary>Оставляет только буквы, цифры, пробел, дефис и подчёркивание.</summary>
    private static string SanitizeIdentifier(string input, string fallback)
    {
        if (string.IsNullOrWhiteSpace(input)) return fallback;

        var cleaned = new string(input
            .Where(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_')
            .ToArray())
            .Trim();

        return cleaned.Length == 0 ? fallback : cleaned;
    }

    /// <summary>
    /// Запускает процесс и читает stdout/stderr ОДНОВРЕМЕННО.
    /// Последовательное чтение вызывает deadlock, когда дочерний процесс
    /// заполняет буфер stderr и блокируется до завершения.
    ///
    /// <para><b>Почему именно такой порядок.</b> StreamReader.ReadToEndAsync
    /// не отменяется после начала чтения: токен проверяется только в момент
    /// старта, поэтому уже начатое чтение нельзя прервать. Поэтому сначала
    /// дожидаемся ВЫХОДА процесса (это отменяется через токен), и только
    /// потом читаем вывод — к этому моменту каналы уже закрыты и чтение
    /// обязано завершиться. Таймаут обязателен: без него зависшая утилита
    /// держит вызывающий код вечно, а отмены у чтения нет.</para>
    /// </summary>
    private static async Task<int> RunAndReportAsync(
        ProcessStartInfo psi, IProgress<string>? progress, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? ExternalCommandTimeout);
        using var proc = new Process { StartInfo = psi };

        try
        {
            if (!proc.Start()) return -1;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Утилита не найдена или недоступна.
            return -1;
        }
        catch (InvalidOperationException)
        {
            return -1;
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
            progress?.Report(
                $"Утилита {psi.FileName} не ответила за {(timeout ?? ExternalCommandTimeout).TotalSeconds:0} с и была прервана.");
            return -1;
        }

        // Процесс завершился, каналы закрыты. Ограничиваем и сам дренаж.
        var reads = Task.WhenAll(stdoutTask, stderrTask);
        if (await Task.WhenAny(reads, Task.Delay(DrainTimeout, cts.Token)).ConfigureAwait(false) != reads)
        {
            TryKill(proc);
            progress?.Report(
                $"Вывод утилиты {psi.FileName} не получен за {DrainTimeout.TotalSeconds:0} с.");
            return -1;
        }

        // reads завершён, поэтому .Result здесь безопасен.
        await reads.ConfigureAwait(false);
        int exitCode = proc.ExitCode;

        if (exitCode != 0 && progress != null)
        {
            var err = stderrTask.Result.Trim();
            if (err.Length > 0)
            {
                if (err.Length > 300) err = err[..300] + "...";
                progress.Report($"Ошибка утилиты (код {exitCode}): {err}");
            }
        }

        return exitCode;
    }
}
