using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.Versioning;
using System.Text.Json;

namespace MotionCommander.Core.Storage;

/// <summary>
/// Сбор РЕАЛЬНЫХ показателей S.M.A.R.T. для консольной утилиты motion.
///
/// <para>Заменяет прежнюю реализацию, которая без обращения к системе
/// возвращала Model = "Windows Certified Storage Controller", HealthPercent = 100,
/// Grade = "A+" и TemperatureC = 36.0 — то есть полностью выдуманный отчёт,
/// не зависящий от номера запрошенного диска.</para>
///
/// <para>Теперь используются штатные источники Windows:
/// MSFT_Disk.HealthStatus, MSFT_StorageReliabilityCounter (root\microsoft\windows\storage)
/// и таблица MSStorageDriver_FailurePredict* (root\wmi). Если контроллер
/// ничего не отдаёт — в отчёте честно указывается «нет данных».</para>
/// </summary>
public static class RealSmartReader
{
    /// <summary>Читает реальные показатели накопителя Windows.</summary>
    // System.Management доступен только в Windows. Core — кроссплатформенная
    // библиотека (используется в Linux/macOS), поэтому помечаем атрибутом
    // и возвращаем «нет данных» вместо исключения на других платформах.
    [SupportedOSPlatform("windows")]
    public static (bool ok, string? model, string? health, double? tempC, double? wearPercent,
                   long? powerOnHours, long? powerCycles, long? readErrors, long? writeErrors,
                   string? source, string note) Read(int diskNumber)
    {
        string note;

        try
        {
            string? model = null;
            string? health = null;

            // ВАЖНО: HealthStatus у MSFT_Disk — это UInt16-ЭНУМ, а не строка:
            //   0 = Unknown, 1 = Healthy, 2 = Warning, 3 = Unhealthy.
            // Раньше он читался как строка, и значение "0" (Unknown) могло быть
            // истолковано как «здоров». Теперь разбираем корректно и при
            // Unknown честно сообщаем, что состояние неизвестно.

            // Явно НЕ вызываем scope.Connect(): на некоторых системах это
            // бросает UnauthorizedAccessException, хотя обычный запрос
            // к тому же пространству имён через PowerShell работает.
            var scope = new ManagementScope(@"\\.\root\microsoft\windows\storage");
            scope.Options.Timeout = TimeSpan.FromSeconds(5);

            using (var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT Number, FriendlyName, HealthStatus FROM MSFT_Disk")))
            {
                using var results = searcher.Get();
                foreach (ManagementObject obj in results)
                {
                    using (obj)
                    {
                        if (Convert.ToInt32(obj["Number"] ?? -1) != diskNumber) continue;

                        model = obj["FriendlyName"]?.ToString();
                        health = ParseHealthStatus(obj["HealthStatus"]);
                        break;
                    }
                }
            }

            if (model == null)
            {
                return (false, null, null, null, null, null, null, null, null, null,
                        "Диск не найден в MSFT_Disk.");
            }

            // ── 2. Счётчики надёжности (температура, износ, наработка) ────
            // Класс MSFT_StorageReliabilityCounter присутствует не на всех
            // системах (в частности, отсутствует на части серверов и в
            // виртуальных средах). Отсутствие — не ошибка: просто
            // фиксируем, что показания недоступны.
            double? tempC = null, wear = null;
            long? poh = null, cycles = null, readErr = null, writeErr = null;
            string source = "MSFT_Disk (WMI)";
            bool counterClassMissing = false;

            try
            {
                using var searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery("SELECT DeviceId, Temperature, Wear, PowerOnHours, " +
                                    "PowerOnCycles, StartStopCycleCount, ReadErrorsTotal, WriteErrorsTotal " +
                                    "FROM MSFT_StorageReliabilityCounter"));

                using var results = searcher.Get();
                foreach (ManagementObject obj in results)
                {
                    using (obj)
                    {
                        if (!BelongsToNumber(obj["DeviceId"]?.ToString(), diskNumber, scope)) continue;

                        // Фильтруем заведомо фиктивные показания датчика:
                        // многие дешёвые контроллеры отдают 0 или 255.
                        if (TryDouble(obj, "Temperature", out double t) && t > 0 && t < 120)
                            tempC = t;

                        if (TryDouble(obj, "Wear", out double w) && w is >= 0 and <= 100)
                            wear = w;

                        if (TryLong(obj, "PowerOnHours", out long h) && h >= 0) poh = h;
                        if (TryLong(obj, "PowerOnCycles", out long pc) && pc >= 0) cycles = pc;
                        if (TryLong(obj, "StartStopCycleCount", out long c) && c >= 0) cycles = c;
                        if (TryLong(obj, "ReadErrorsTotal", out long r)) readErr = r;
                        if (TryLong(obj, "WriteErrorsTotal", out long w2)) writeErr = w2;

                        if (tempC.HasValue || wear.HasValue || poh.HasValue)
                            source = "MSFT_StorageReliabilityCounter (WMI)";
                        break;
                    }
                }
            }
            catch (ManagementException)
            {
                counterClassMissing = true;
            }

            bool any = tempC.HasValue || wear.HasValue || poh.HasValue
                       || readErr.HasValue || writeErr.HasValue;

            note = any
                ? "Показатели прочитаны из счётчиков надёжности Windows."
                : counterClassMissing
                    ? "Класс MSFT_StorageReliabilityCounter отсутствует в системе (типично для серверов, виртуальных сред и части RAID-контроллеров)."
                    : "Контроллер не публикует счётчики надёжности (типично для RAID, виртуальных дисков и USB-хабов без прозрачного S.M.A.R.T.).";

            return (true, model, health, tempC, wear, poh, cycles, readErr, writeErr, source, note);
        }
        catch (ManagementException ex)
        {
            return (false, null, null, null, null, null, null, null, null, null,
                    $"WMI недоступен: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (false, null, null, null, null, null, null, null, null, null,
                    $"Не удалось прочитать данные накопителя: {ex.Message}");
        }
    }

    /// <summary>
    /// Разбирает MSFT_Disk.HealthStatus (UInt16-энум) в читаемую строку.
    /// Возвращает null, если контроллер сообщил Unknown (0) — это НЕ «здоров».
    /// </summary>
    private static string? ParseHealthStatus(object? raw)
    {
        if (raw == null) return null;

        if (!ushort.TryParse(raw.ToString(), out ushort code)) return null;

        return code switch
        {
            1 => "Healthy",
            2 => "Warning",
            3 => "Unhealthy",
            _ => null   // 0 = Unknown — состояние не определено
        };
    }

    /// <summary>Сопоставляет DeviceId счётчика с номером диска через MSFT_Disk.</summary>
    [SupportedOSPlatform("windows")]
    private static bool BelongsToNumber(string? deviceId, int diskNumber, ManagementScope scope)
    {
        if (string.IsNullOrEmpty(deviceId)) return false;

        try
        {
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery($"SELECT Number FROM MSFT_Disk WHERE DeviceId='{deviceId.Replace("'", "''")}'"));

            using var results = searcher.Get();
            foreach (ManagementObject obj in results)
            {
                using (obj)
                {
                    if (Convert.ToInt32(obj["Number"] ?? -1) == diskNumber) return true;
                }
            }
        }
        catch { }

        return false;
    }

    /// <summary>
    /// Читает реальные данные через smartctl (Linux) с разбором JSON-вывода.
    /// Возвращает false, если smartctl не установлен или не поддерживает -j.
    /// </summary>
    public static (bool ok, string? model, string? health, double? tempC, double? wearPercent,
                   long? powerOnHours, string note) ReadSmartctl(string devicePath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "smartctl",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-a");
            psi.ArgumentList.Add("-j");
            psi.ArgumentList.Add(devicePath);

            using var proc = new Process { StartInfo = psi };
            if (!proc.Start()) return (false, null, null, null, null, null, "smartctl не найден.");

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            Task.WaitAll(new Task[] { stdoutTask, stderrTask }, 20000);
            var output = stdoutTask.Result;

            if (string.IsNullOrWhiteSpace(output))
                return (false, null, null, null, null, null, "smartctl не вернул данных.");

            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;

            string? model = null;
            if (root.TryGetProperty("model_name", out var mn))
                model = mn.GetString();

            double? temp = null;
            if (root.TryGetProperty("temperature", out var t))
            {
                if (t.TryGetProperty("current", out var tc) && tc.TryGetDouble(out double tv))
                {
                    // smartctl отдаёт температуру в градусах Цельсия.
                    if (tv > 0 && tv < 120) temp = tv;
                }
            }

            long? powerOnHours = null;
            if (root.TryGetProperty("power_on_time", out var pot) &&
                pot.TryGetProperty("hours", out var poh) &&
                poh.TryGetInt64(out long pohVal) && pohVal >= 0)
            {
                powerOnHours = pohVal;
            }

            // Ресурс SSD: percentage_used в smartctl означает ИЗНОС (100 = конец).
            double? wear = null;
            if (root.TryGetProperty("nvme_smart_health_information_log", out var nvme) &&
                nvme.TryGetProperty("percentage_used", out var pu) &&
                pu.TryGetDouble(out double puVal) && puVal is >= 0 and <= 100)
            {
                wear = puVal;
            }

            // Здоровье выводим только из реального вердикта smartctl,
            // а не подставляем 100 по умолчанию.
            string? health = null;
            if (root.TryGetProperty("smart_status", out var ss) && ss.TryGetProperty("passed", out var passed))
                health = passed.GetBoolean() ? "Passed" : "Failed";

            bool any = temp.HasValue || wear.HasValue || powerOnHours.HasValue || health != null;

            return (any, model, health, temp, wear, powerOnHours,
                    any ? "smartctl -a -j (реальный опрос контроллера)"
                        : "smartctl не сообщил показателей надёжности.");
        }
        catch (JsonException)
        {
            return (false, null, null, null, null, null, "smartctl вернул нераспознаваемый вывод.");
        }
        catch (Exception ex)
        {
            return (false, null, null, null, null, null, $"Ошибка запуска smartctl: {ex.Message}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool TryDouble(ManagementObject obj, string prop, out double value)
    {
        value = 0;
        try
        {
            object? raw = obj[prop];
            return raw != null
                && double.TryParse(raw.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
        catch { return false; }
    }

    [SupportedOSPlatform("windows")]
    private static bool TryLong(ManagementObject obj, string prop, out long value)
    {
        value = 0;
        try
        {
            object? raw = obj[prop];
            return raw != null
                && long.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }
        catch { return false; }
    }
}
