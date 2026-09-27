using Win11CopyDialog.Helpers;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using Win11CopyDialog.Modules.StorageControlCenter.Models;

namespace Win11CopyDialog.Modules.StorageControlCenter.Services;

/// <summary>
/// Сбор РЕАЛЬНЫХ показателей надёжности накопителя.
///
/// <para><b>Важно.</b> Этот класс не занимается «оценкой» и не подставляет
/// значения по умолчанию. Каждый показатель либо измеряется системой, либо
/// помечается как недоступный, и в интерфейсе отображается «Нет данных».</para>
///
/// <para>Используемые реальные источники на Windows:</para>
/// <list type="bullet">
/// <item><c>MSFT_StorageReliabilityCounter</c> (root\microsoft\windows\storage) —
/// температура, износ, наработка, счётчики ошибок, циклы питания. Это
/// основной источник для NVMe и современных SATA SSD.</item>
/// <item><c>MSFT_Disk</c> — реальные HealthStatus и OperationalStatus.</item>
/// <item><c>MSStorageDriver_FailurePredictData</c> / <c>...Thresholds</c> /
/// <c>...Status</c> (root\wmi) — настоящая таблица S.M.A.R.T. для ATA/NVMe.</item>
/// <item><c>fsutil behavior query DisableDeleteNotify</c> — реальное состояние TRIM.</item>
/// <item><c>defrag /A</c> — реальная фрагментация.</item>
/// </list>
///
/// <para>Если контроллер ничего не отдаёт (RAID, виртуальные диски, USB-хабы
/// без прозрачного S.M.A.R.T.), это НЕ ошибка: приложение честно сообщает,
/// что данные недоступны, вместо того чтобы выдумывать их.</para>
/// </summary>
public static class SmartHealthService
{
    private const string StorageNamespace = @"\\.\root\microsoft\windows\storage";
    private const string WmiNamespace = @"\\.\root\wmi";

    public static void EnrichDiskHealth(StorageDisk disk)
    {
        var notes = new List<string>();

        bool gotRaw = false;

        // 1. ПРЯМОЕ чтение S.M.A.R.T. с самого диска через IOCTL.
        //
        //    Это единственный источник, который работает там, где штатные
        //    счётчики Windows молчат. Проверено на этой машине: класс
        //    MSFT_StorageReliabilityCounter существует, но возвращает НОЛЬ
        //    экземпляров, а классов MSStorageDriver_FailurePredictData в
        //    root\wmi нет вообще. Поэтому порядок именно такой: сначала
        //    настоящий диск, и только потом запасные источники.
        //
        //    Проверка прав идёт ДО чтения, иначе при каждом обновлении
        //    списка накопителей в интерфейсе появлялось бы сообщение
        //    «запустите от имени администратора» — вводит в заблуждение,
        //    потому что программа обычно запускается с повышенными правами.
        if (!SuperAdminPrivilegeHelper.IsAdministrator())
        {
            disk.SmartNeedsAdministrator = true;
            notes.Add("Чтение S.M.A.R.T. напрямую с накопителя требует прав администратора. " +
                      "Запустите Motion Commander от имени администратора.");
        }
        else
        {
            gotRaw = TryReadDirectSmart(disk, notes);
        }

        // 2. Реальные счётчики надёжности (температура, износ, наработка, ошибки).
        bool gotReliability = TryQueryReliabilityCounter(disk, notes);

        // 3. Настоящая таблица S.M.A.R.T. через WMI, когда он есть.
        bool gotSmart = TryReadSmartAttributes(disk, notes);

        // 4. Реальное состояние TRIM.
        TryQueryTrimState(disk, notes);

        // 5. Фиксируем источник данных. Прямое чтение с диска — самый
        //    достоверный уровень, поэтому приоритет у него наивысший.
        disk.Source = gotRaw || gotSmart ? TelemetrySource.Smart
                     : gotReliability ? TelemetrySource.Wmi
                     : TelemetrySource.Unavailable;

        if (notes.Count > 0)
        {
            disk.TelemetryNote = string.Join(" ", notes);
        }
    }

    /// <summary>
    /// Читает S.M.A.R.T. напрямую с накопителя и разносит значения по полям.
    ///
    /// <para>Температура, износ и наработка берутся из конкретных атрибутов
    /// S.M.A.R.T. по их идентификаторам. Ничего не вычисляется «примерно»:
    /// если атрибута нет, поле остаётся не измеренным, и интерфейс покажет
    /// «н/д» вместо правдоподобного числа.</para>
    /// </summary>
    private static bool TryReadDirectSmart(StorageDisk disk, List<string> notes)
    {
        try
        {
            var raw = SmartRawReader.Read(disk.DiskNumber, out var failure);
            if (raw == null)
            {
                if (failure != null)
                {
                    // Отказ в доступе — это не «диск не поддерживает», а
                    // «нет прав». Разница принципиальна: в первом случае
                    // подсказывать перезапуск с повышенными правами.
                    if (failure.NeedsAdministrator)
                    {
                        disk.SmartNeedsAdministrator = true;
                        notes.Add("S.M.A.R.T. читается напрямую с диска и требует прав администратора. " +
                                  "Запустите Motion Commander от имени администратора.");
                    }
                    else
                    {
                        notes.Add(failure.Reason);
                    }
                }
                return false;
            }

            if (!raw.IsPlausible)
            {
                // Контроллер вернул пустышку (типично для RAID и виртуальных
                // дисков). Показывать нули как «диск здоров» нельзя.
                notes.Add("Накопитель вернул пустую таблицу S.M.A.R.T. — так бывает у RAID-контроллеров и виртуальных дисков. Данные не выдуманы.");
                return false;
            }

            ApplyParsedSmartBlock(disk, raw.Data512);
            disk.SmartOverallPass = raw.OverallPass;
            return disk.SmartAttributes.Count > 0;
        }
        catch (Exception ex)
        {
            notes.Add($"Прямое чтение S.M.A.R.T. не удалось: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Разбирает блок 512 байт S.M.A.R.T. и заполняет накопитель.
    /// </summary>
    /// <remarks>
    /// <para>Вынесено отдельно, чтобы разбор можно было проверить на
    /// эталонных данных без физического доступа к диску. Раньше проверка
    /// требовала настоящего накопителя с правами администратора, то есть
    /// на практике не выполнялась — и ошибка в разборе жила незамеченной.</para>
    ///
    /// <para>Если блок оказывается пустышкой (RAID, виртуальный диск),
    /// накопитель остаётся без данных: подставлять правдоподобные
    /// значения вместо неизмеренных нельзя.</para>
    /// </remarks>
    public static void ApplyParsedSmartBlock(StorageDisk disk, byte[] block)
    {
        disk.SmartAttributes.Clear();

        if (!SmartRawParser.HasPlausibleAttributes(block))
            return;

        var attrs = ParseRawAttributes(block);
        disk.SmartAttributes = attrs;
        disk.HasSmartAttributes = attrs.Count > 0;

        ApplyDerivedHealthValues(disk, attrs, new List<string>());
    }

    /// <summary>
    /// Разбирает блок 512 байт в список атрибутов.
    /// </summary>
    /// <remarks>
    /// Разметка записи по 12 байт: [0] идентификатор, [3] текущее значение,
    /// [4] худшее, [5] порог, [6..11] сырое значение (младшие байты первыми).
    /// </remarks>
    private static List<SmartAttribute> ParseRawAttributes(byte[] block)
    {
        var result = new List<SmartAttribute>();

        for (int offset = 0; offset + 11 < block.Length && offset < 30 * 12; offset += 12)
        {
            byte id = block[offset];
            if (id == 0) continue;

            byte current = block[offset + 3];
            byte worst = block[offset + 4];
            byte threshold = block[offset + 5];

            // Нормализованное значение вне 1..253 — признак заглушки.
            if (current == 0 || current == 0xFF) continue;

            // Смещение offset обязательно: сырое значение лежит в блоке
            // [offset+6 .. offset+11] своего атрибута. Без него читались
            // байты первого атрибута для всех остальных, и наработка,
            // температура и состояние секторов показывались бы чужыми
            // значениями.
            long rawValue = 0;
            for (int b = 11; b >= 6; b--) rawValue = (rawValue << 8) | block[offset + b];

            bool hasThreshold = threshold > 0 && threshold < 0xFF;

            var attr = new SmartAttribute
            {
                Id = id,
                Name = SmartAttributeCatalog.GetName(id),
                Description = SmartAttributeCatalog.GetDescription(id),
                Current = current,
                Worst = worst,
                Threshold = threshold,
                HasThreshold = hasThreshold,
                RawValue = rawValue,
                RawValueFormatted = SmartAttributeCatalog.FormatRawValue(id, rawValue)
            };

            attr.Status = SmartAttribute.StatusFromThreshold(current, threshold, hasThreshold);
            attr.IsCritical = attr.Status == "Critical" && SmartAttributeCatalog.IsCriticalAttribute(id);

            // Атрибут без названия и без данных пользователю не нужен.
            if (attr.Name.Length > 0 || hasThreshold || rawValue > 0)
                result.Add(attr);
        }

        return result;
    }

    /// <summary>
    /// Выводит температуру, износ и наработку из конкретных атрибутов S.M.A.R.T.
    /// </summary>
    /// <remarks>
    /// Идентификаторы соответствуют спецификации S.M.A.R.T.:
    /// 5 — Reallocated Sectors (перераспределённые сектора),
    /// 9 — Power-On Hours (наработка в часах),
    /// 194 — Temperature (температура, обычно в градусах Цельсия),
    /// 197 — Current Pending Sector (сектора ожидающие переприсвоения),
    /// 231/233 — износ SSD: оставшийся ресурс в процентах,
    /// 202 — Percent Lifetime Used, где меньше значит лучше.
    /// </remarks>
    private static void ApplyDerivedHealthValues(
        StorageDisk disk, List<SmartAttribute> attrs, List<string> notes)
    {
        // Температура.
        //
        // Атрибут 194 (Temperature_Celsius) хранит градусы Цельсия
        // непосредственно, а 190 (Airflow_Temperature) и 235 (NVMe) — в
        // десятых долях. Раньше оба случая обрабатывались одинаково и
        // брался младший байт, из-за чего 34 °C превращались в 3 °C.
        var t194 = attrs.FirstOrDefault(x => x.Id == 194);
        if (t194 != null)
        {
            long celsius = t194.RawValue & 0xFF;
            if (celsius is >= 5 and <= 120)
            {
                disk.TemperatureC = celsius;
                disk.HasTemperature = true;
                disk.TemperatureSource = $"S.M.A.R.T. атрибут 194 «{t194.Name}», прочитан с диска";
            }
        }

        if (!disk.HasTemperature)
        {
            var t190 = attrs.FirstOrDefault(x => x.Id == 190);
            if (t190 != null)
            {
                double celsius = t190.RawValue / 10.0;
                if (celsius is >= 1 and <= 120)
                {
                    disk.TemperatureC = celsius;
                    disk.HasTemperature = true;
                    disk.TemperatureSource = $"S.M.A.R.T. атрибут 190 «{t190.Name}», прочитан с диска";
                }
            }
        }

        if (!disk.HasTemperature)
        {
            var t235 = attrs.FirstOrDefault(x => x.Id == 235);
            if (t235 != null)
            {
                double celsius = t235.RawValue / 10.0;
                if (celsius is >= 1 and <= 120)
                {
                    disk.TemperatureC = celsius;
                    disk.HasTemperature = true;
                    disk.TemperatureSource = $"S.M.A.R.T. атрибут 235 «{t235.Name}», прочитан с диска";
                }
            }
        }

        // Износ SSD. Атрибут 202 — «сколько ресурса израсходовано»
        // (меньше значит лучше), а 231 и 233 — «сколько осталось»
        // (больше значит лучше). Путать их нельзя: значение из
        // противоположного атрибута выглядело бы правдоподобно и
        // показывало бы износ наоборот.
        var used = attrs.FirstOrDefault(x => x.Id == 202);
        if (used != null && used.Current is > 0 and < 255)
        {
            disk.WearLevelPercent = used.Current;
            disk.HasWear = true;
            disk.WearSource = $"S.M.A.R.T. атрибут 202 «{used.Name}», прочитан с диска";
        }
        else
        {
            var remaining = attrs.FirstOrDefault(x => x.Id is 231 or 233);
            if (remaining != null && remaining.Current is > 0 and < 255)
            {
                disk.WearLevelPercent = 100.0 - remaining.Current;
                disk.HasWear = true;
                disk.WearSource = $"S.M.A.R.T. атрибут {remaining.Id} «{remaining.Name}», прочитан с диска";
            }
        }

        // Наработка в часах берётся из сырого значения атрибута 9, а не
        // из нормализованного: часы хранятся именно в сыром поле.
        // Нормализованное значение 253 означает «предварительный подсчёт»,
        // при котором счётчик часов ещё не накопился.
        var poh = attrs.FirstOrDefault(x => x.Id == 9);
        if (poh != null)
        {
            long hours = poh.RawValue & 0xFFFFFFFF;
            if (hours > 0 && poh.Current < 253)
            {
                disk.PowerOnHours = hours;
                disk.HasPowerOnHours = true;
            }
        }

        // Состояние секторов — прямой признак начинающейся деградации.
        var realloc = attrs.FirstOrDefault(x => x.Id == 5);
        if (realloc != null && realloc.Current is > 0 and < 100)
            disk.ReallocatedSectors = realloc.RawValue & 0xFFFFFFFF;

        var pending = attrs.FirstOrDefault(x => x.Id == 197);
        if (pending != null && pending.Current is > 0 and < 100)
            disk.PendingSectors = pending.RawValue & 0xFFFFFFFF;

        var uncorrectable = attrs.FirstOrDefault(x => x.Id is 187 or 198);
        if (uncorrectable != null && uncorrectable.Current is > 0 and < 100)
            disk.UncorrectableSectors = uncorrectable.RawValue & 0xFFFFFFFF;
    }

    /// <summary>
    /// Читает MSFT_StorageReliabilityCounter. Возвращает true, если получены
    /// хотя бы какие-то реальные значения.
    /// </summary>
    private static bool TryQueryReliabilityCounter(StorageDisk disk, List<string> notes)
    {
        try
        {
            var scope = new ManagementScope(StorageNamespace);
            scope.Connect();

            // ВАЖНО: DeviceId здесь — строка вида "\\.\PHYSICALDRIVE0",
            // поэтому фильтровать по нему нельзя (старый код сравнивал его
            // с целым DiskNumber, из-за чего выборка всегда была пустой).
            // Берём все счётчики и сопоставляем через MSFT_Disk ниже.
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT DeviceId, Temperature, Wear, PowerOnHours, " +
                                "PowerOnCycles? AS PowerOnCycles, ReadErrorsTotal, WriteErrorsTotal, " +
                                "FlushErrorsTotal, StartStopCycleCount FROM MSFT_StorageReliabilityCounter"));

            using var results = searcher.Get();
            int matched = 0;

            foreach (ManagementBaseObject obj in results)
            {
                using (obj)
                {
                    if (!BelongsToDisk(obj, disk, scope)) continue;

                    matched++;

                    if (TryGetDouble(obj, "Temperature", out double temp) && IsPlausibleTemperature(temp))
                    {
                        disk.TemperatureC = temp;
                        disk.HasTemperature = true;
                    }

                    if (TryGetDouble(obj, "Wear", out double wear) && wear is >= 0 and <= 100)
                    {
                        disk.WearLevelPercent = wear;
                        disk.HasWear = true;
                    }

                    if (TryGetLong(obj, "PowerOnHours", out long poh) && poh >= 0)
                    {
                        disk.PowerOnHours = poh;
                        disk.HasPowerOnHours = true;
                    }

                    if (TryGetLong(obj, "StartStopCycleCount", out long cycles) && cycles >= 0)
                    {
                        disk.PowerCycles = cycles;
                        disk.HasPowerCycles = true;
                    }

                    long readErr = 0, writeErr = 0;
                    bool gotRead = TryGetLong(obj, "ReadErrorsTotal", out readErr);
                    bool gotWrite = TryGetLong(obj, "WriteErrorsTotal", out writeErr);

                    if (gotRead || gotWrite)
                    {
                        disk.ReadErrorsTotal = readErr;
                        disk.WriteErrorsTotal = writeErr;
                        disk.HasErrorCounts = true;
                    }
                }
            }

            if (matched == 0)
            {
                notes.Add("Счётчики надёжности (MSFT_StorageReliabilityCounter) недоступны для этого накопителя.");
                return false;
            }

            return disk.HasTemperature || disk.HasWear || disk.HasPowerOnHours || disk.HasErrorCounts;
        }
        catch (ManagementException)
        {
            notes.Add("WMI-хранилище накопителей недоступно (нет прав или служба не запущена).");
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            notes.Add("Нет прав доступа к WMI для чтения счётчиков надёжности.");
            return false;
        }
        catch
        {
            notes.Add("Не удалось прочитать счётчики надёжности накопителя.");
            return false;
        }
    }

    /// <summary>
    /// Сопоставляет счётчик с конкретным диском через MSFT_Disk,
    /// у которого есть и DeviceId, и Number.
    /// </summary>
    private static bool BelongsToDisk(ManagementBaseObject counter, StorageDisk disk, ManagementScope scope)
    {
        string? deviceId = counter["DeviceId"]?.ToString();
        if (string.IsNullOrEmpty(deviceId)) return false;

        try
        {
            using var diskSearcher = new ManagementObjectSearcher(scope,
                new ObjectQuery($"SELECT Number FROM MSFT_Disk WHERE DeviceId = '{EscapeWql(deviceId)}'"));

            using var disks = diskSearcher.Get();
            foreach (ManagementBaseObject d in disks)
            {
                using (d)
                {
                    if (TryGetInt(d, "Number", out int number) && number == disk.DiskNumber)
                        return true;
                }
            }
        }
        catch
        {
            // Если сопоставить не удалось — считаем, что диск не этот.
            return false;
        }

        return false;
    }

    /// <summary>
    /// Читает настоящую таблицу S.M.A.R.T. через root\wmi.
    /// Работает для ATA/NVMe, когда драйвер диска публикует предиктивные данные.
    /// </summary>
    private static bool TryReadSmartAttributes(StorageDisk disk, List<string> notes)
    {
        try
        {
            // Получаем InstanceName (например "\\.\PHYSICALDRIVE0")
            string? instanceName = GetSmartInstanceName(disk);
            if (string.IsNullOrEmpty(instanceName))
            {
                notes.Add("S.M.A.R.T. недоступен: драйвер не публикует предиктивные данные (типично для RAID и виртуальных дисков).");
                return false;
            }

            // 1. Пороги (не у всех контроллеров есть)
            var thresholds = new Dictionary<byte, int>();
            var tScope = new ManagementScope(WmiNamespace);
            tScope.Connect();

            using (var tSearcher = new ManagementObjectSearcher(tScope,
                new ObjectQuery($"SELECT InstanceName, ActiveThreshold FROM MSStorageDriver_FailurePredictThresholds WHERE InstanceName='{EscapeWql(instanceName)}'")))
            using (var tResults = tSearcher.Get())
            {
                foreach (ManagementBaseObject t in tResults)
                {
                    using (t)
                    {
                        if (t["ActiveThreshold"] is ushort[] arr)
                        {
                            for (int i = 0; i < arr.Length && i < 256; i++)
                            {
                                if (arr[i] != 0) thresholds[(byte)i] = arr[i];
                            }
                        }
                    }
                }
            }

            // 2. Данные атрибутов
            var scope = new ManagementScope(WmiNamespace);
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery($"SELECT Active, InstanceName, VendorSpecific FROM MSStorageDriver_FailurePredictData WHERE InstanceName='{EscapeWql(instanceName)}'"));

            using var results = searcher.Get();
            var attributes = new List<SmartAttribute>();

            foreach (ManagementBaseObject obj in results)
            {
                using (obj)
                {
                    if (obj["VendorSpecific"] is not ushort[] raw) continue;

                    // Каждые 12 байт = один атрибут (S.M.A.R.T. ATA).
                    for (int offset = 0; offset + 11 < raw.Length; offset += 12)
                    {
                        byte id = (byte)raw[offset];
                        if (id == 0) continue;

                        // Байты 3,4,5: Current / Worst / Threshold
                        int current = raw[offset + 3];
                        int worst = raw[offset + 4];
                        int threshold = raw[offset + 5];

                        // Байты 6..11: Raw Value (6 байт, младшие байты первыми)
                        long rawValue = 0;
                        for (int b = 11; b >= 6; b--)
                        {
                            rawValue = (rawValue << 8) | raw[offset + b];
                        }

                        bool hasThreshold = thresholds.TryGetValue(id, out int thr) || threshold > 0;
                        if (thresholds.TryGetValue(id, out thr)) threshold = thr;

                        var attr = new SmartAttribute
                        {
                            Id = id,
                            Name = SmartAttributeCatalog.GetName(id),
                            Description = SmartAttributeCatalog.GetDescription(id),
                            Current = current,
                            Worst = worst,
                            Threshold = threshold,
                            HasThreshold = hasThreshold,
                            RawValue = rawValue,
                            RawValueFormatted = SmartAttributeCatalog.FormatRawValue(id, rawValue)
                        };

                        attr.Status = SmartAttribute.StatusFromThreshold(current, threshold, hasThreshold);
                        attr.IsCritical = attr.Status == "Critical"
                                          && SmartAttributeCatalog.IsCriticalAttribute(id);

                        // Атрибут без имени в каталоге и без значимых данных не показываем.
                        if (attr.Name.Length > 0 && (hasThreshold || rawValue > 0 || current > 0))
                        {
                            attributes.Add(attr);
                        }
                    }
                }
            }

            if (attributes.Count == 0)
            {
                notes.Add("Таблица S.M.A.R.T. пуста или не поддерживается контроллером.");
                return false;
            }

            disk.SmartAttributes = attributes;
            disk.HasSmartAttributes = true;

            // Надёжность: если контроллер сообщает «непредсказуемое» состояние.
            TryReadPredictStatus(instanceName, disk, notes);

            return true;
        }
        catch (ManagementException)
        {
            notes.Add("S.M.A.R.T. недоступен через WMI.");
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>InstanceName для SMART: «\\.\PHYSICALDRIVE{n}».</summary>
    private static string? GetSmartInstanceName(StorageDisk disk)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT InstanceName FROM MSStorageDriver_FailurePredictStatus");

            using var results = searcher.Get();
            foreach (ManagementBaseObject obj in results)
            {
                using (obj)
                {
                    string? name = obj["InstanceName"]?.ToString();
                    if (name != null &&
                        name.EndsWith($"PHYSICALDRIVE{disk.DiskNumber}", StringComparison.OrdinalIgnoreCase))
                    {
                        return name;
                    }
                }
            }
        }
        catch
        {
            // Нет класса предиктивного статуса — SMART не поддерживается.
        }

        return null;
    }

    private static void TryReadPredictStatus(string instanceName, StorageDisk disk, List<string> notes)
    {
        try
        {
            var scope = new ManagementScope(WmiNamespace);
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery($"SELECT PredictFailure, Reason FROM MSStorageDriver_FailurePredictStatus WHERE InstanceName='{EscapeWql(instanceName)}'"));

            using var results = searcher.Get();
            foreach (ManagementBaseObject obj in results)
            {
                using (obj)
                {
                    if (TryGetInt(obj, "PredictFailure", out int predictFailure) && predictFailure != 0)
                    {
                        disk.HealthStatus = "Unhealthy";
                        notes.Add($"S.M.A.R.T. прогнозирует отказ: {obj["Reason"]}.");
                    }
                }
            }
        }
        catch
        {
            // Отсутствие предиктивного статуса — не ошибка.
        }
    }

    /// <summary>
    /// Реально проверяет состояние TRIM через fsutil. Раньше флаги
    /// IsTrimSupported/IsTrimEnabled просто выставлялись в true по типу
    /// накопителя, без какого-либо обращения к системе.
    /// </summary>
    private static void TryQueryTrimState(StorageDisk disk, List<string> notes)
    {
        try
        {
            // fsutil без буквы диска показывает глобальную политику NTFS.
            var (exitCode, output) = RunProcess("fsutil.exe", new[] { "behavior", "query", "DisableDeleteNotify" }, 15_000);

            if (exitCode != 0)
            {
                notes.Add("Не удалось определить состояние TRIM (fsutil недоступен).");
                return;
            }

            // «DisableDeleteNotify = 0» означает, что TRIM РАЗРЕШЁН.
            bool trimEnabled = !string.IsNullOrEmpty(output) &&
                               !output.Contains("DisableDeleteNotify = 1", StringComparison.OrdinalIgnoreCase);

            disk.IsTrimSupported = trimEnabled || MediaSupportsTrim(disk.MediaType);
            disk.IsTrimEnabled = trimEnabled;
            disk.HasTrimInfo = true;
        }
        catch
        {
            notes.Add("Проверка TRIM не удалась.");
        }
    }

    private static bool MediaSupportsTrim(StoragePhysicalMedia media) =>
        media is StoragePhysicalMedia.NVMeSSD or StoragePhysicalMedia.SataSSD;

    /// <summary>
    /// Запуск утилиты с одновременным чтением stdout и stderr.
    ///
    /// <para>Раньше здесь стояло Task.WaitAll(..., 5000), а затем
    /// stdoutTask.Result без проверки результата ожидания. Если чтение не
    /// успевало завершиться, .Result блокировался НАВСЕГДА. Сейчас
    /// используется WhenAny с пределом, и .Result вызывается только у
    /// гарантированно завершённых задач.</para>
    /// </summary>
    private static (int exitCode, string output) RunProcess(string exe, string[] args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = new Process { StartInfo = psi };
            if (!proc.Start()) return (-1, "");

            // Обе задачи чтения стартуют ОДНОВРЕМЕННО — иначе возможен
            // классический pipe deadlock при заполнении буфера stderr.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit(timeoutMs))
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                return (-1, "");
            }

            // Процесс завершился, каналы закрыты. Ждём освобождения буферов
            // с пределом и НЕ блокируемся на .Result при незавершённой задаче.
            var reads = Task.WhenAll(stdoutTask, stderrTask);
            if (!reads.Wait(DrainTimeoutMs)) return (proc.ExitCode, "");

            return (proc.ExitCode, stdoutTask.Result + "\n" + stderrTask.Result);
        }
        catch
        {
            return (-1, "");
        }
    }

    /// <summary>Предел ожидания освобождения каналов после завершения процесса.</summary>
    private static readonly int DrainTimeoutMs = 5000;

    // ── Вспомогательные методы разбора значений ──────────────────────────────

    private static bool TryGetDouble(ManagementBaseObject obj, string prop, out double value)
    {
        value = 0;
        try
        {
            object? raw = obj[prop];
            if (raw == null) return false;
            return double.TryParse(raw.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
        catch { return false; }
    }

    private static bool TryGetLong(ManagementBaseObject obj, string prop, out long value)
    {
        value = 0;
        try
        {
            object? raw = obj[prop];
            if (raw == null) return false;
            return long.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }
        catch { return false; }
    }

    private static bool TryGetInt(ManagementBaseObject obj, string prop, out int value)
    {
        value = 0;
        try
        {
            object? raw = obj[prop];
            if (raw == null) return false;
            return int.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }
        catch { return false; }
    }

    /// <summary>
    /// Фильтрует заведомо недостоверные показания датчика.
    /// Многие дешёвые контроллеры отдают 0 или 255 вместо реальной температуры.
    /// </summary>
    private static bool IsPlausibleTemperature(double c) => c is > 0 and < 120;

    /// <summary>Экранирует значение для безопасной подстановки в WQL-запрос.</summary>
    private static string EscapeWql(string value) => value.Replace("\\", "\\\\").Replace("'", "''");
}
