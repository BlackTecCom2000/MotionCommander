using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Win11CopyDialog.Modules.GameTuning;

public sealed class ConfigChange
{
    public string Key { get; init; } = "";
    public string? OldValue { get; init; }
    public string NewValue { get; init; } = "";
    public bool Found { get; init; }

    public string Line => Found
        ? $"{Key}: {OldValue ?? "—"} → {NewValue}"
        : $"{Key}: ключа нет в файле — не добавляется";
}

public sealed class ConfigPreview
{
    public string PresetId { get; init; } = "";
    public string Title { get; init; } = "";
    public string FilePath { get; init; } = "";
    public bool FileExists { get; init; }
    public bool GameRunning { get; init; }
    public List<ConfigChange> Changes { get; init; } = new();
    public string Note { get; init; } = "";

    public int FoundCount => Changes.Count(c => c.Found);
    public string Summary => !FileExists
        ? "Файл настроек не найден: игра ещё не запускалась, либо установлена в другое место."
        : FoundCount == 0
            ? "Ни одного из ожидаемых ключей в файле нет — запись будет отменена, чтобы не добавлять неизвестные параметры."
            : $"Будет изменено ключей: {FoundCount} из {Changes.Count}. Остальные строки файла не трогаются.";
}

public sealed class ConfigPresetInfo
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string FileHint { get; init; } = "";
}

/// <summary>
/// Правка конфигурационных файлов игр.
///
/// Жёсткие правила:
/// <list type="bullet">
/// <item>меняются только ключи, которые уже есть в файле — неизвестные
/// параметры не добавляются (иначе игра может не запуститься);</item>
/// <item>перед записью создаётся резервная копия рядом с файлом;</item>
/// <item>после записи файл перечитывается и значения проверяются;</item>
/// <item>если файла нет (игра не запускалась) — никакой «настройки по
/// шаблону» не создаётся, интерфейс честно сообщает об этом.</item>
/// </list>
/// </summary>
public static class GameConfigPresets
{
    public const string Mk11Fps = "mk11_fps";
    public const string Mk11Balanced = "mk11_balanced";
    public const string Cs2Fps = "cs2_fps";

    private static readonly Regex IniLine = new(
        @"^(?<lead>\s*)(?<key>[^=;#\[\]\s][^=]*?)(?<sep>\s*=\s*)(?<value>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex KeyValuesLine = new(
        @"^(?<lead>\s*)""(?<key>[^""]+)""(?<sep>\s+)""(?<value>[^""]*)""(?<tail>.*)$",
        RegexOptions.Compiled);

    public static IReadOnlyList<ConfigPresetInfo> Presets { get; } = new[]
    {
        new ConfigPresetInfo
        {
            Id = Mk11Fps,
            Title = "Mortal Kombat 11 — максимум FPS",
            Description = "Тени, постобработка и сглаживание на минимум, лимит кадров = частоте экрана, VSync выключен.",
            FileHint = @"%LOCALAPPDATA%\MK11\Saved\Config\WindowsNoEditor\GameUserSettings.ini"
        },
        new ConfigPresetInfo
        {
            Id = Mk11Balanced,
            Title = "Mortal Kombat 11 — баланс",
            Description = "Средние тени и постобработка: картинка сохраняется, нагрузка ниже максимальной.",
            FileHint = @"%LOCALAPPDATA%\MK11\Saved\Config\WindowsNoEditor\GameUserSettings.ini"
        },
        new ConfigPresetInfo
        {
            Id = Cs2Fps,
            Title = "Counter-Strike 2 — низкая задержка",
            Description = "VSync и тройная буферизация выключены, полноэкранный режим включён, низкая задержка NVIDIA Reflex.",
            FileHint = @"<папка игры>\game\csgo\cfg\cs2_video.txt"
        }
    };

    public static ConfigPreview BuildPreview(string presetId, string? gameExe, int refreshHz)
    {
        string file = ResolveFile(presetId, gameExe) ?? "";
        return BuildPreviewForFile(presetId, file, refreshHz, gameExe);
    }

    /// <summary>
    /// Предпросмотр для явно указанного файла. Отделён от <see cref="BuildPreview"/>,
    /// чтобы правку можно было проверить на временном файле, не трогая конфиги игр.
    /// </summary>
    public static ConfigPreview BuildPreviewForFile(string presetId, string filePath, int refreshHz, string? gameExe = null)
    {
        ConfigPresetInfo? info = Presets.FirstOrDefault(p => p.Id == presetId);
        bool exists = !string.IsNullOrEmpty(filePath) && File.Exists(filePath);

        var changes = new List<ConfigChange>();
        if (exists)
        {
            (string text, _) = ReadText(filePath);
            changes = BuildChanges(presetId, refreshHz)
                .Select(edit => FindChange(presetId, text, edit.Key, edit.Value))
                .ToList();
        }
        else
        {
            changes = BuildChanges(presetId, refreshHz)
                .Select(edit => new ConfigChange { Key = edit.Key, NewValue = edit.Value, Found = false })
                .ToList();
        }

        return new ConfigPreview
        {
            PresetId = presetId,
            Title = info?.Title ?? presetId,
            FilePath = filePath ?? "",
            FileExists = exists,
            GameRunning = gameExe != null && IsProcessRunning(Path.GetFileNameWithoutExtension(gameExe)),
            Changes = changes,
            Note = presetId == Cs2Fps
                ? "CS2 перезаписывает cs2_video.txt при выходе — игра должна быть закрыта. Запрещённые Valve команды не добавляются."
                : "Игра должна быть закрыта, иначе она перезапишет файл при выходе. Применяются только существующие ключи."
        };
    }

    public static TweakOperationResult Apply(string presetId, string? gameExe, int refreshHz)
    {
        string? file = ResolveFile(presetId, gameExe);
        if (string.IsNullOrEmpty(file))
            return TweakOperationResult.Fail("Не найден файл настроек игры: выберите игру или укажите её папку.");

        return ApplyToFile(presetId, file, refreshHz, gameExe);
    }

    /// <summary>
    /// Применяет пресет к конкретному файлу. Используется и интерфейсом
    /// (после ResolveFile), и проверками на временном файле.
    /// </summary>
    public static TweakOperationResult ApplyToFile(string presetId, string filePath, int refreshHz, string? gameExe = null)
    {
        if (string.IsNullOrEmpty(filePath))
            return TweakOperationResult.Fail("Не указан файл настроек.");

        if (!Presets.Any(p => p.Id == presetId))
            return TweakOperationResult.Fail("Неизвестный пресет: " + presetId);

        if (!File.Exists(filePath))
            return TweakOperationResult.Fail("Файл настроек ещё не создан: запустите игру один раз, выйдите и повторите.");

        if (!string.IsNullOrWhiteSpace(gameExe))
        {
            string processName = Path.GetFileNameWithoutExtension(gameExe);
            if (!string.IsNullOrEmpty(processName) && IsProcessRunning(processName))
                return TweakOperationResult.Fail($"Игра {processName} запущена: закройте её, иначе она перезапишет файл при выходе.");
        }

        (string original, Encoding encoding) = ReadText(filePath);

        var edits = BuildChanges(presetId, refreshHz);
        (string updated, List<ConfigChange> applied) = ApplyEdits(presetId, original, edits);

        if (applied.Count(c => c.Found) == 0)
        {
            return TweakOperationResult.Fail(
                "Ни один ожидаемый ключ не найден. Файл не изменён: добавлять неизвестные параметры в конфиг игры нельзя.");
        }

        if (string.Equals(original, updated, StringComparison.Ordinal))
        {
            return TweakOperationResult.Success(
                $"Файл уже в целевом состоянии ({applied.Count(c => c.Found)} ключей), запись не потребовалась.");
        }

        string backup = filePath + ".mc-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        try
        {
            File.Copy(filePath, backup, overwrite: false);
            File.WriteAllText(filePath, updated, encoding);
        }
        catch (Exception ex)
        {
            return TweakOperationResult.Fail("Не удалось записать конфиг: " + ex.Message);
        }

        // Проверка после записи: перечитываем файл и убеждаемся, что значения
        // действительно изменились, а не только что команда записи не упала.
        (string readBack, _) = ReadText(filePath);
        var verify = applied.Where(c => c.Found)
            .Select(c => FindChange(presetId, readBack, c.Key, c.NewValue))
            .ToList();

        int ok = verify.Count(c => c.Found && string.Equals(c.OldValue, c.NewValue, StringComparison.OrdinalIgnoreCase));
        if (ok != verify.Count)
        {
            return TweakOperationResult.Fail(
                $"Запись выполнена, но проверка не прошла: подтверждено {ok} из {verify.Count} ключей. " +
                $"Резервная копия: {Path.GetFileName(backup)}");
        }

        return TweakOperationResult.Success(
            $"Изменено ключей: {ok}. Резервная копия: {Path.GetFileName(backup)}. Значения проверены повторным чтением.");
    }

    public static string? FindLatestBackup(string presetId, string? gameExe)
    {
        string? file = ResolveFile(presetId, gameExe);
        return string.IsNullOrEmpty(file) ? null : FindLatestBackupForFile(file);
    }

    public static string? FindLatestBackupForFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return null;

        string? dir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;

        return Directory.GetFiles(dir, Path.GetFileName(filePath) + ".mc-backup-*")
                        .OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault();
    }

    public static TweakOperationResult Restore(string presetId, string? gameExe)
    {
        string? file = ResolveFile(presetId, gameExe);
        if (string.IsNullOrEmpty(file))
            return TweakOperationResult.Fail("Файл настроек не найден.");

        return RestoreFile(file);
    }

    public static TweakOperationResult RestoreFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return TweakOperationResult.Fail("Файл настроек не найден.");

        string? backup = FindLatestBackupForFile(filePath);
        if (string.IsNullOrEmpty(backup) || !File.Exists(backup))
            return TweakOperationResult.Fail("Резервная копия этого пресета не найдена: откатывать нечего.");

        try
        {
            File.Copy(backup, filePath, overwrite: true);
            return TweakOperationResult.Success("Конфиг восстановлен из копии: " + Path.GetFileName(backup));
        }
        catch (Exception ex)
        {
            return TweakOperationResult.Fail("Не удалось восстановить конфиг: " + ex.Message);
        }
    }

    public static string? ResolveFile(string presetId, string? gameExe)
    {
        if (presetId is Mk11Fps or Mk11Balanced)
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "MK11", "Saved", "Config", "WindowsNoEditor", "GameUserSettings.ini");
        }

        if (presetId == Cs2Fps)
        {
            if (string.IsNullOrWhiteSpace(gameExe) || !File.Exists(gameExe)) return null;

            string? gameRoot = null;
            DirectoryInfo? dir = new FileInfo(gameExe).Directory;
            for (int i = 0; i < 6 && dir != null; i++)
            {
                if (string.Equals(dir.Name, "game", StringComparison.OrdinalIgnoreCase))
                {
                    gameRoot = dir.FullName;
                    break;
                }
                dir = dir.Parent;
            }

            if (gameRoot == null) return null;
            return Path.Combine(gameRoot, "csgo", "cfg", "cs2_video.txt");
        }

        return null;
    }

    private static List<(string Key, string Value)> BuildChanges(string presetId, int refreshHz)
    {
        string fps = refreshHz > 0 ? refreshHz.ToString(CultureInfo.InvariantCulture) : "0";

        if (presetId == Mk11Fps)
        {
            return new List<(string, string)>
            {
                ("sg.ShadowQuality", "1"),
                ("sg.PostProcessQuality", "1"),
                ("sg.AntiAliasingQuality", "1"),
                ("sg.EffectsQuality", "2"),
                ("sg.FoliageQuality", "1"),
                ("sg.ResolutionQuality", "100"),
                ("FrameRateLimit", fps),
                ("bUseVSync", "False"),
                ("FullscreenMode", "0")
            };
        }

        if (presetId == Mk11Balanced)
        {
            return new List<(string, string)>
            {
                ("sg.ShadowQuality", "2"),
                ("sg.PostProcessQuality", "2"),
                ("sg.AntiAliasingQuality", "2"),
                ("sg.EffectsQuality", "2"),
                ("sg.FoliageQuality", "2"),
                ("sg.ResolutionQuality", "100"),
                ("FrameRateLimit", fps),
                ("bUseVSync", "False"),
                ("FullscreenMode", "0")
            };
        }

        return new List<(string, string)>
        {
            ("setting.mat_vsync", "0"),
            ("setting.mat_tripplebuffered", "0"),
            ("setting.fullscreen", "1"),
            ("setting.r_low_latency", "2")
        };
    }

    private static ConfigChange FindChange(string presetId, string text, string key, string newValue)
    {
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (presetId == Cs2Fps)
            {
                Match m = KeyValuesLine.Match(line);
                if (m.Success && string.Equals(m.Groups["key"].Value, key, StringComparison.OrdinalIgnoreCase))
                    return new ConfigChange { Key = key, OldValue = m.Groups["value"].Value, NewValue = newValue, Found = true };
            }
            else
            {
                Match m = IniLine.Match(line);
                if (m.Success && string.Equals(m.Groups["key"].Value.Trim(), key, StringComparison.OrdinalIgnoreCase))
                    return new ConfigChange { Key = key, OldValue = m.Groups["value"].Value.Trim(), NewValue = newValue, Found = true };
            }
        }

        return new ConfigChange { Key = key, NewValue = newValue, Found = false };
    }

    private static (string Text, List<ConfigChange> Applied) ApplyEdits(
        string presetId,
        string text,
        List<(string Key, string Value)> edits)
    {
        string[] lines = text.Split('\n');
        var applied = new List<ConfigChange>();
        var remaining = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string value) in edits) remaining[key] = value;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            bool crlf = line.EndsWith('\r');
            string body = crlf ? line[..^1] : line;

            if (presetId == Cs2Fps)
            {
                Match m = KeyValuesLine.Match(body);
                if (!m.Success) continue;

                string key = m.Groups["key"].Value;
                if (!remaining.TryGetValue(key, out string? newValue)) continue;
                if (string.Equals(m.Groups["value"].Value, newValue, StringComparison.Ordinal)) 
                {
                    applied.Add(new ConfigChange { Key = key, OldValue = m.Groups["value"].Value, NewValue = newValue, Found = true });
                    remaining.Remove(key);
                    continue;
                }

                body = $"{m.Groups["lead"].Value}\"{key}\"{m.Groups["sep"].Value}\"{newValue}\"{m.Groups["tail"].Value}";
                lines[i] = body + (crlf ? "\r" : "");
                applied.Add(new ConfigChange { Key = key, OldValue = m.Groups["value"].Value, NewValue = newValue, Found = true });
                remaining.Remove(key);
                continue;
            }

            Match ini = IniLine.Match(body);
            if (!ini.Success) continue;

            string iniKey = ini.Groups["key"].Value.Trim();
            if (!remaining.TryGetValue(iniKey, out string? iniValue)) continue;

            string oldValue = ini.Groups["value"].Value.Trim();
            if (string.Equals(oldValue, iniValue, StringComparison.Ordinal))
            {
                applied.Add(new ConfigChange { Key = iniKey, OldValue = oldValue, NewValue = iniValue, Found = true });
                remaining.Remove(iniKey);
                continue;
            }

            body = ini.Groups["lead"].Value + ini.Groups["key"].Value + ini.Groups["sep"].Value + iniValue;
            lines[i] = body + (crlf ? "\r" : "");
            applied.Add(new ConfigChange { Key = iniKey, OldValue = oldValue, NewValue = iniValue, Found = true });
            remaining.Remove(iniKey);
        }

        return (string.Join("\n", lines), applied);
    }

    private static (string Text, Encoding Encoding) ReadText(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        Encoding encoding = new UTF8Encoding(false);

        int offset = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            encoding = new UTF8Encoding(true);
            offset = 3;
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            encoding = Encoding.Unicode;
            offset = 2;
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            encoding = Encoding.BigEndianUnicode;
            offset = 2;
        }

        return (encoding.GetString(bytes, offset, bytes.Length - offset), encoding);
    }

    public static bool IsProcessRunning(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        try
        {
            return System.Diagnostics.Process.GetProcessesByName(processName).Length > 0;
        }
        catch
        {
            return false;
        }
    }
}
