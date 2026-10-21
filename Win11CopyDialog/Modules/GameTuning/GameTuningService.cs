using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Win11CopyDialog.Helpers;

namespace Win11CopyDialog.Modules.GameTuning;

public sealed class FpsSessionStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;

    public FpsSessionStore(string path) => _path = path;

    public List<FpsResult> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new List<FpsResult>();
            return JsonSerializer.Deserialize<List<FpsResult>>(File.ReadAllText(_path), Options)
                   ?? new List<FpsResult>();
        }
        catch
        {
            return new List<FpsResult>();
        }
    }

    public void Append(FpsResult result)
    {
        var all = Load();
        all.Add(result);
        if (all.Count > 100) all.RemoveRange(0, all.Count - 100);
        Save(all);
    }

    public void Save(List<FpsResult> results)
    {
        string? dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(results, Options));
        File.Move(tmp, _path, overwrite: true);
    }

    public FpsResult? Latest(string processName)
        => Load().LastOrDefault(r => string.Equals(r.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Единственная точка применения настроек: снимок исходных значений,
/// проверка результата, журнал и откат.
/// </summary>
public sealed class GameTuningService
{
    public static GameTuningService Instance { get; } = new();

    private readonly GameTuningBackupStore _backups;
    private readonly FpsSessionStore _fpsSessions;
    private readonly object _logGate = new();

    private GameTuningService()
    {
        string dir = AppPaths.WritableDataDirectory;
        _backups = new GameTuningBackupStore(Path.Combine(dir, "game-tuning-backup.json"));
        _fpsSessions = new FpsSessionStore(Path.Combine(dir, "game-fps-sessions.json"));
    }

    public IReadOnlyList<GameTweak> Tweaks => GameTweakCatalog.All;
    public FpsSessionStore FpsSessions => _fpsSessions;
    public string BackupPath => _backups.Path;
    public int BackupCount => _backups.Count;

    public static bool IsElevated => SuperAdminPrivilegeHelper.IsAdministrator();

    public static bool IsElevationSupported => true;

    public TweakStatusInfo GetStatus(GameTweak tweak, string? gameExe)
    {
        try
        {
            return tweak.ReadStatus(gameExe);
        }
        catch (Exception ex)
        {
            return new TweakStatusInfo
            {
                Status = TweakStatus.Unknown,
                Detail = "Ошибка чтения состояния: " + ex.Message
            };
        }
    }

    public TweakOperationResult Apply(GameTweak tweak, string? gameExe)
    {
        if (tweak.Scope == TweakScope.PerGame && string.IsNullOrWhiteSpace(gameExe))
            return TweakOperationResult.Fail("Сначала выберите игру.");

        if (tweak.RequiresAdmin && !IsElevated)
            return TweakOperationResult.Fail("Нужны права администратора: перезапустите программу с повышением прав.");

        TweakBackupEntry? existing = _backups.Find(tweak.Id, gameExe);

        if (!tweak.IsAction && existing == null)
        {
            var entry = new TweakBackupEntry
            {
                TweakId = tweak.Id,
                GameExe = gameExe ?? ""
            };

            if (tweak.Id == GameTweakCatalog.IdPower)
            {
                (string guid, _) = PowerScheme.GetActive();
                entry.PowerSchemeGuid = guid;
                entry.HadPowerScheme = !string.IsNullOrEmpty(guid);
            }

            foreach (RegistryTarget target in tweak.SnapshotTargetsFor(gameExe))
                entry.Values.Add(RegistryIo.Capture(target));

            _backups.Put(entry);
        }

        TweakOperationResult result = tweak.Apply(gameExe);
        Audit(result.Ok ? "APPLY" : "APPLY-FAILED", tweak.Id, gameExe, result.Message);
        return result;
    }

    public TweakOperationResult Revert(GameTweak tweak, string? gameExe)
    {
        TweakBackupEntry? entry = _backups.Find(tweak.Id, gameExe);

        if (entry == null)
        {
            TweakOperationResult direct;
            if (tweak.IsAction)
            {
                direct = TweakOperationResult.Fail("Разовое действие не требует отката.");
            }
            else
            {
                direct = tweak.Revert(gameExe);
            }

            Audit(direct.Ok ? "REVERT-DEFAULT" : "REVERT-FAILED", tweak.Id, gameExe, direct.Message);
            return direct;
        }

        var failures = new List<string>();

        if (entry.HadPowerScheme)
        {
            if (!PowerScheme.TryActivate(entry.PowerSchemeGuid, out string powerError))
                failures.Add("схема питания: " + powerError);
        }

        foreach (RegistrySnapshot snapshot in entry.Values)
        {
            if (!RegistryIo.Restore(snapshot, out string error))
                failures.Add(snapshot.TargetId + ": " + error);
        }

        if (failures.Count > 0)
        {
            string message = "Откат выполнен не полностью: " + string.Join("; ", failures);
            Audit("REVERT-PARTIAL", tweak.Id, gameExe, message);
            return TweakOperationResult.Fail(message);
        }

        _backups.Remove(tweak.Id, gameExe);
        string ok = $"Исходные значения восстановлены ({entry.Values.Count} значений" +
                    (entry.HadPowerScheme ? " + схема питания" : "") + ").";
        Audit("REVERT", tweak.Id, gameExe, ok);
        return TweakOperationResult.Success(ok);
    }

    public List<(string TweakId, string Message)> RevertAll()
    {
        var results = new List<(string, string)>();
        foreach (TweakBackupEntry entry in _backups.Load().ToList())
        {
            GameTweak? tweak = Tweaks.FirstOrDefault(t =>
                string.Equals(t.Id, entry.TweakId, StringComparison.OrdinalIgnoreCase));

            if (tweak == null)
            {
                results.Add((entry.TweakId, "твик больше не существует — пропущен"));
                continue;
            }

            TweakOperationResult result = Revert(tweak, string.IsNullOrEmpty(entry.GameExe) ? null : entry.GameExe);
            results.Add((entry.TweakId, result.Message));
        }

        if (results.Count == 0)
            results.Add(("—", "Нечего откатывать: исходные значения не сохранялись."));

        return results;
    }

    public List<GameRecommendation> BuildRecommendations(HardwareInfo hardware, string? gameExe)
    {
        var list = new List<GameRecommendation>();

        void Consider(GameTweak tweak, string basis, string action)
        {
            TweakStatusInfo status = GetStatus(tweak, gameExe);
            if (status.Status == TweakStatus.Applied || status.Status == TweakStatus.Unavailable) return;
            list.Add(new GameRecommendation
            {
                TweakId = tweak.Id,
                Title = tweak.Title,
                Basis = basis,
                Action = action
            });
        }

        GameTweak? Find(string id) => Tweaks.FirstOrDefault(t => t.Id == id);

        if (Find(GameTweakCatalog.IdPower) is { } power)
        {
            TweakStatusInfo status = GetStatus(power, gameExe);
            Consider(power, $"Активная схема питания: {status.Current}.", "Включить высокую производительность");
        }

        if (Find(GameTweakCatalog.IdDvr) is { } dvr)
        {
            TweakStatusInfo status = GetStatus(dvr, gameExe);
            Consider(dvr, status.Current + ".", "Выключить фоновую запись");
        }

        if (Find(GameTweakCatalog.IdGameMode) is { } gameMode)
        {
            TweakStatusInfo status = GetStatus(gameMode, gameExe);
            Consider(gameMode, status.Current + ".", "Включить режим игры");
        }

        if (Find(GameTweakCatalog.IdWindowed) is { } windowed)
        {
            TweakStatusInfo status = GetStatus(windowed, gameExe);
            Consider(windowed, $"Сборка Windows: {hardware.WindowsBuild}. {status.Current}.", "Включить оптимизации оконных игр");
        }

        bool lowRam = hardware.RamGb is double ram && ram < 16;
        bool integratedGpu = hardware.Gpus.Any(g =>
            g.Name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
            g.Name.Contains("UHD", StringComparison.OrdinalIgnoreCase) ||
            g.Name.Contains("Vega", StringComparison.OrdinalIgnoreCase));

        if (lowRam || integratedGpu)
        {
            if (Find(GameTweakCatalog.IdVisualFx) is { } visual)
            {
                TweakStatusInfo status = GetStatus(visual, gameExe);
                string basis = lowRam
                    ? $"ОЗУ {hardware.RamLine} — меньше 16 ГБ. {status.Current}."
                    : $"Обнаружена встроенная графика: {hardware.GpuLine}. {status.Current}.";
                Consider(visual, basis, "Отключить эффекты оформления");
            }
        }

        if (Find(GameTweakCatalog.IdMouse) is { } mouse)
        {
            TweakStatusInfo status = GetStatus(mouse, gameExe);
            Consider(mouse, status.Current + ". На FPS не влияет — только на ввод.", "Выключить ускорение мыши");
        }

        if (hardware.RamGb is double ramGb && ramGb <= 8 && IsElevated)
        {
            if (Find(GameTweakCatalog.IdStandbyTrim) is { } trim)
                list.Add(new GameRecommendation
                {
                    TweakId = trim.Id,
                    Title = trim.Title,
                    Basis = $"ОЗУ {hardware.RamLine} — 8 ГБ или меньше. Разовое действие, FPS напрямую не повышает.",
                    Action = "Освободить standby-кэш"
                });
        }

        if (!string.IsNullOrWhiteSpace(gameExe))
        {
            if (Find(GameTweakCatalog.IdGpuPreference) is { } gpuPref && hardware.HasDiscreteGpu)
            {
                TweakStatusInfo status = GetStatus(gpuPref, gameExe);
                Consider(gpuPref, $"Обнаружено несколько GPU: {hardware.GpuLine}. {status.Current}.", "Выбрать дискретную GPU");
            }

            if (Find(GameTweakCatalog.IdFullscreenOptimizations) is { } fso)
            {
                TweakStatusInfo status = GetStatus(fso, gameExe);
                Consider(fso, $"Для {Path.GetFileName(gameExe)}: {status.Current}.", "Отключить оптимизации полного экрана");
            }
        }

        return list;
    }

    public TweakOperationResult ApplyRecommended(HardwareInfo hardware, string? gameExe)
    {
        List<GameRecommendation> recommendations = BuildRecommendations(hardware, gameExe);
        if (recommendations.Count == 0)
            return TweakOperationResult.Success("Рекомендаций нет: рекомендуемые настройки уже применены.");

        var ok = new List<string>();
        var failed = new List<string>();

        foreach (GameRecommendation recommendation in recommendations)
        {
            GameTweak? tweak = Tweaks.FirstOrDefault(t => t.Id == recommendation.TweakId);
            if (tweak == null) continue;

            if (tweak.RequiresAdmin && !IsElevated)
            {
                failed.Add($"{tweak.Title}: нужны права администратора");
                continue;
            }

            TweakOperationResult result = Apply(tweak, gameExe);
            if (result.Ok) ok.Add(tweak.Title);
            else failed.Add($"{tweak.Title}: {result.Message}");
        }

        string message = $"Применено: {ok.Count}" +
                         (failed.Count > 0 ? $", не удалось: {failed.Count} ({string.Join("; ", failed)})" : "") + ".";
        return failed.Count == 0
            ? TweakOperationResult.Success(message)
            : TweakOperationResult.Fail(message);
    }

    public bool HasBackup(string tweakId, string? gameExe)
        => _backups.Find(tweakId, gameExe) != null;

    public string BuildStatusReport(HardwareInfo hardware, string? gameExe)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("GAME TUNING STATUS");
        sb.AppendLine(new string('=', 60));
        sb.AppendLine("CPU:  " + hardware.CpuLine);
        sb.AppendLine("RAM:  " + hardware.RamLine);
        sb.AppendLine("GPU:  " + hardware.GpuLine);
        sb.AppendLine("Экран: " + hardware.DisplayLine);
        sb.AppendLine("ОС:   " + hardware.OsLine);
        sb.AppendLine("Права администратора: " + (hardware.IsAdmin ? "да" : "нет"));
        sb.AppendLine("Резервных копий настроек: " + BackupCount);
        sb.AppendLine();

        foreach (IGrouping<TweakGroup, GameTweak> group in Tweaks.GroupBy(t => t.Group))
        {
            sb.AppendLine($"[{group.Key}]");
            foreach (GameTweak tweak in group)
            {
                TweakStatusInfo status = GetStatus(tweak, gameExe);
                sb.AppendLine($"  {tweak.Id,-34} {status.StatusText,-14} {status.Current}" +
                              (string.IsNullOrWhiteSpace(status.Detail) ? "" : " | " + status.Detail));
            }
            sb.AppendLine();
        }

        List<GameRecommendation> recommendations = BuildRecommendations(hardware, gameExe);
        sb.AppendLine("РЕКОМЕНДАЦИИ (с измеренным основанием):");
        if (recommendations.Count == 0)
        {
            sb.AppendLine("  нет");
        }
        else
        {
            foreach (GameRecommendation r in recommendations)
                sb.AppendLine($"  - {r.Title}: {r.Basis} → {r.Action}");
        }

        return sb.ToString();
    }

    public void Audit(string action, string tweakId, string? gameExe, string message)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDirectory);
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {action} {tweakId}" +
                          (string.IsNullOrEmpty(gameExe) ? "" : " exe=" + gameExe) +
                          " | " + message + Environment.NewLine;

            lock (_logGate)
            {
                File.AppendAllText(Path.Combine(AppPaths.LogsDirectory, "game-tuning.log"), line);
            }
        }
        catch
        {
            // Журнал не должен ломать применение настроек.
        }
    }
}
