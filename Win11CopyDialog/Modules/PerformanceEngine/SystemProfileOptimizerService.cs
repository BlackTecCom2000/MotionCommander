using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Win11CopyDialog.Modules.GameTuning;

namespace Win11CopyDialog.Modules.PerformanceEngine;

public enum OptimizationProfileId
{
    Balanced,       // Сбалансированный (штатный Windows)
    Gaming,         // 🎮 Игры и минимальная задержка
    Quiet,          // 🍃 Тихая работа и энергосбережение
    FileTransfer    // 🚀 Копирование файлов и максимальный I/O
}

public sealed class ProfileDefinition
{
    public OptimizationProfileId Id { get; set; }
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Tagline { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> MeasurableSettings { get; set; } = new();
    public string ExpectedEffect { get; set; } = "";
    public string SafetyNote { get; set; } = "Безопасно. Настройки обратимы в 1 клик.";
}

public sealed class SystemProfileBackupState
{
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string OriginalPowerSchemeGuid { get; set; } = "";
    public string OriginalPowerSchemeName { get; set; } = "";
    public OptimizationProfileId AppliedProfileId { get; set; } = OptimizationProfileId.Balanced;
}

public static class SystemProfileOptimizerService
{
    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MotionCommander");
    private static readonly string BackupFilePath = Path.Combine(AppDataDir, "system_profile_backup.json");
    private static readonly string AuditLogFilePath = Path.Combine(AppDataDir, "system_audit_log.txt");

    // Стандартные GUID схем питания Windows
    public const string SchemeBalanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    public const string SchemeHighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public const string SchemePowerSaver = "a1841308-3541-4fab-bc81-f71556f20b4a";
    public const string SchemeUltimatePerformance = "e9a42b02-d5df-448d-aa00-03f14749eb61";

    public static OptimizationProfileId CurrentProfile { get; private set; } = OptimizationProfileId.Balanced;
    public static bool HasBackup => File.Exists(BackupFilePath);

    public static List<ProfileDefinition> GetAvailableProfiles()
    {
        return new List<ProfileDefinition>
        {
            new()
            {
                Id = OptimizationProfileId.Gaming,
                Name = "Игры и минимальная задержка",
                Icon = "🎮",
                Tagline = "Схема питания без энергосбережения",
                Description = "Переключает только схему электропитания Windows на «Высокая производительность». " +
                              "Парковка ядер, приоритет ввода-вывода и TRIM этим профилем не настраиваются. " +
                              "Настройки самих игр и измерение FPS — на вкладке «Игры».",
                MeasurableSettings = new()
                {
                    "Схема электропитания: Высокая производительность (powercfg /setactive)",
                    "Проверка: активная схема перечитывается после переключения"
                },
                ExpectedEffect = "Схема запрещает простой ядер и снижение частоты во время игры. " +
                                 "Средний FPS обычно не меняется; возможен эффект на 1% low. " +
                                 "Прирост этим профилем не измеряется — для честного замера используйте вкладку «Игры».",
                SafetyNote = "Схема питания обратима: исходная схема сохраняется в резервную копию."
            },
            new()
            {
                Id = OptimizationProfileId.Quiet,
                Name = "Тихая работа и энергосбережение",
                Icon = "🍃",
                Tagline = "Штатная схема «Экономия энергии»",
                Description = "Переключает только схему электропитания Windows на «Экономия энергии». " +
                              "Максимальное состояние процессора, таймауты накопителей и прочие параметры схемы не изменяются.",
                MeasurableSettings = new()
                {
                    "Схема электропитания: Энергосбережение (powercfg /setactive)",
                    "Проверка: активная схема перечитывается после переключения"
                },
                ExpectedEffect = "Windows ограничивает буст и энергопотребление штатными средствами схемы. " +
                                 "Температура и уровень шума программой не измеряются: фактический эффект зависит от охлаждения.",
                SafetyNote = "Схема питания обратима: исходная схема сохраняется в резервную копию."
            },
            new()
            {
                Id = OptimizationProfileId.FileTransfer,
                Name = "Копирование файлов и максимальный I/O",
                Icon = "🚀",
                Tagline = "Схема питания без засыпания накопителей",
                Description = "Переключает только схему электропитания на «Высокая производительность». " +
                              "ReTrim, размер буфера и приоритеты ввода-вывода этот профиль не настраивает.",
                MeasurableSettings = new()
                {
                    "Схема электропитания: Высокая производительность (powercfg /setactive)",
                    "Проверка: активная схема перечитывается после переключения"
                },
                ExpectedEffect = "Схема не даёт накопителю уходить в сон во время длинной операции. " +
                                 "Прирост скорости копирования этим профилем не измерялся и не гарантируется.",
                SafetyNote = "Схема питания обратима: исходная схема сохраняется в резервную копию."
            },
            new()
            {
                Id = OptimizationProfileId.Balanced,
                Name = "Сбалансированный режим (Windows по умолчанию)",
                Icon = "⚖",
                Tagline = "Стандартный баланс скорости и энергопотребления",
                Description = "Штатный профиль операционной системы Windows 11 с динамическим регулированием частот по запросу приложений.",
                MeasurableSettings = new()
                {
                    "Схема электропитания: Сбалансированная (Balanced)",
                    "Проверка: активная схема перечитывается после переключения",
                    "Прочие параметры Windows не изменяются"
                },
                ExpectedEffect = "Стандартная работа всех компонентов ПК без специфических твиков.",
                SafetyNote = "Исходное состояние Windows."
            }
        };
    }

    public static async Task<(bool success, string message)> ApplyProfileAsync(OptimizationProfileId profileId)
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);

            // 1. Создаем резервную копию текущего состояния (если её еще нет)
            if (!HasBackup)
            {
                await CreateBackupSnapshotAsync();
            }

            // 2. Применяем схему электропитания — единственное, что меняет профиль.
            string targetSchemeGuid = profileId switch
            {
                OptimizationProfileId.Gaming => SchemeHighPerformance,
                OptimizationProfileId.Quiet => SchemePowerSaver,
                OptimizationProfileId.FileTransfer => SchemeHighPerformance,
                _ => SchemeBalanced
            };

            (int exitCode, string output) = await RunPowerCfgAsync($"/setactive {targetSchemeGuid}");
            if (exitCode != 0)
            {
                string reason = string.IsNullOrWhiteSpace(output)
                    ? $"powercfg завершился с кодом {exitCode}"
                    : output;
                await AppendAuditLogAsync($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ОШИБКА схемы питания для {profileId}: {reason}");
                return (false, "Не удалось переключить схему питания: " + reason);
            }

            // Проверка после команды: заявлять об успехе без перечитывания нельзя.
            (string activeGuid, string activeName) = PowerScheme.GetActive();
            if (!string.Equals(activeGuid, targetSchemeGuid, StringComparison.OrdinalIgnoreCase))
            {
                string actual = string.IsNullOrEmpty(activeGuid)
                    ? "активную схему прочитать не удалось"
                    : $"активна другая схема: {activeName} ({activeGuid})";
                await AppendAuditLogAsync($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ПРОВЕРКА НЕ ПРОЙДЕНА для {profileId}: {actual}");
                return (false, "Схема питания не подтвердилась: " + actual + ".");
            }

            CurrentProfile = profileId;

            // 3. Запись в журнал аудита
            string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Применён профиль: {profileId}. " +
                              $"Схема питания: {PowerScheme.FriendlyName(activeGuid)} ({activeGuid}), проверено. " +
                              "Другие параметры Windows не изменялись. Исходная схема сохранена в резервную копию.";
            await AppendAuditLogAsync(logEntry);

            return (true, $"Профиль «{profileId}»: схема питания переключена на «{PowerScheme.FriendlyName(activeGuid)}» (проверено). " +
                          "Другие параметры Windows не изменялись.");
        }
        catch (Exception ex)
        {
            await AppendAuditLogAsync($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ОШИБКА применения профиля {profileId}: {ex.Message}");
            return (false, $"Ошибка применения профиля: {ex.Message}");
        }
    }

    public static async Task<(bool success, string message)> RollbackToOriginalAsync()
    {
        try
        {
            string targetGuid = SchemeBalanced;
            SystemProfileBackupState? state = null;

            if (File.Exists(BackupFilePath))
            {
                string json = await File.ReadAllTextAsync(BackupFilePath);
                state = JsonSerializer.Deserialize<SystemProfileBackupState>(json);
                if (state != null && !string.IsNullOrWhiteSpace(state.OriginalPowerSchemeGuid))
                    targetGuid = state.OriginalPowerSchemeGuid;
            }

            (int exitCode, string output) = await RunPowerCfgAsync($"/setactive {targetGuid}");
            if (exitCode != 0)
            {
                string reason = string.IsNullOrWhiteSpace(output)
                    ? $"powercfg завершился с кодом {exitCode}"
                    : output;
                await AppendAuditLogAsync($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ОШИБКА отката: {reason}");
                return (false, "Не удалось восстановить схему питания: " + reason);
            }

            (string activeGuid, string activeName) = PowerScheme.GetActive();
            bool verified = string.Equals(activeGuid, targetGuid, StringComparison.OrdinalIgnoreCase);

            CurrentProfile = activeGuid switch
            {
                SchemeHighPerformance or SchemeUltimatePerformance => OptimizationProfileId.Gaming,
                SchemePowerSaver => OptimizationProfileId.Quiet,
                _ => OptimizationProfileId.Balanced
            };

            if (!verified)
            {
                // Копию не удаляем: откат не подтверждён, и исходные данные ещё нужны.
                string actual = string.IsNullOrEmpty(activeGuid)
                    ? "активную схему прочитать не удалось"
                    : $"{activeName} ({activeGuid})";
                await AppendAuditLogAsync($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ОТКАТ НЕ ПОДТВЕРЖДЁН: ожидалась {targetGuid}, {actual}.");
                return (false, "Откат не подтверждён: " + actual + ". Резервная копия сохранена.");
            }

            // Удаляем файл бэкапа только после подтверждённого отката.
            try { File.Delete(BackupFilePath); } catch { }

            string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Откат подтверждён: схема питания «{PowerScheme.FriendlyName(activeGuid)}» ({activeGuid})" +
                              (state != null ? $", состояние от {state.CreatedAt:dd.MM.yyyy HH:mm}" : ", резервная копия отсутствовала") + ".";
            await AppendAuditLogAsync(logEntry);

            return (true, $"Схема питания восстановлена: «{PowerScheme.FriendlyName(activeGuid)}» (проверено). " +
                          "Другие параметры Windows этим профилем не менялись.");
        }
        catch (Exception ex)
        {
            await AppendAuditLogAsync($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ОШИБКА отката настроек: {ex.Message}");
            return (false, $"Не удалось восстановить исходные настройки: {ex.Message}");
        }
    }

    private static async Task CreateBackupSnapshotAsync()
    {
        (string currentGuid, string currentName) = PowerScheme.GetActive();
        if (string.IsNullOrWhiteSpace(currentGuid))
        {
            // Без снимка исходной схемы откат невозможен. Менять настройку
            // «наугад» нельзя: пользователь потеряет своё значение.
            throw new InvalidOperationException(
                "не удалось прочитать текущую схему питания — изменение отменено, чтобы не потерять исходное значение");
        }

        var state = new SystemProfileBackupState
        {
            CreatedAt = DateTime.Now,
            OriginalPowerSchemeGuid = currentGuid,
            OriginalPowerSchemeName = string.IsNullOrWhiteSpace(currentName)
                ? PowerScheme.FriendlyName(currentGuid)
                : currentName,
            AppliedProfileId = CurrentProfile
        };

        string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(BackupFilePath, json);
    }

    private static async Task<(int ExitCode, string Output)> RunPowerCfgAsync(string arguments)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powercfg.exe",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        proc.Start();
        string output = await proc.StandardOutput.ReadToEndAsync();
        output += await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, output.Trim());
    }

    private static async Task AppendAuditLogAsync(string entry)
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            await File.AppendAllTextAsync(AuditLogFilePath, entry + Environment.NewLine);
        }
        catch { }
    }

    public static async Task<List<string>> GetRecentAuditLogAsync(int maxLines = 15)
    {
        try
        {
            if (!File.Exists(AuditLogFilePath)) return new List<string> { "Журнал аудита чист: системные настройки не изменялись." };
            var lines = await File.ReadAllLinesAsync(AuditLogFilePath);
            var result = new List<string>();
            for (int i = lines.Length - 1; i >= 0 && result.Count < maxLines; i--)
            {
                if (!string.IsNullOrWhiteSpace(lines[i])) result.Add(lines[i]);
            }
            return result;
        }
        catch
        {
            return new List<string> { "Не удалось прочитать журнал аудита." };
        }
    }
}
