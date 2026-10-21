using System;
using Microsoft.Win32;

namespace Win11CopyDialog.Modules.GameTuning;

public enum RegHive
{
    CurrentUser,
    LocalMachine
}

/// <summary>
/// Одно конкретное значение реестра. Идентификатор вида
/// «HKCU|Software\...\Key|ValueName» используется в резервных копиях,
/// чтобы откат возвращал ровно то, что было до изменения.
/// </summary>
public sealed class RegistryTarget
{
    public RegHive Hive { get; init; } = RegHive.CurrentUser;
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";

    public RegistryTarget() { }

    public RegistryTarget(RegHive hive, string path, string name)
    {
        Hive = hive;
        Path = path;
        Name = name;
    }

    public static RegistryTarget Hkcu(string path, string name) => new(RegHive.CurrentUser, path, name);
    public static RegistryTarget Hklm(string path, string name) => new(RegHive.LocalMachine, path, name);

    public string Id => $"{Hive}|{Path}|{Name}";

    public override string ToString()
        => $@"{(Hive == RegHive.CurrentUser ? "HKCU" : "HKLM")}\{Path}\{Name}";

    public static RegistryTarget Parse(string id)
    {
        string[] parts = id.Split('|');
        if (parts.Length != 3)
            throw new FormatException("Некорректный идентификатор значения реестра: " + id);

        RegHive hive = parts[0] switch
        {
            "CurrentUser" or "HKCU" => RegHive.CurrentUser,
            "LocalMachine" or "HKLM" => RegHive.LocalMachine,
            _ => throw new FormatException("Неизвестная ветвь реестра: " + parts[0])
        };

        return new RegistryTarget(hive, parts[1], parts[2]);
    }
}

/// <summary>
/// Снимок значения реестра: было ли оно вообще, какого типа и с какими данными.
/// Без «было ли» откат неотличим от установки значения по умолчанию: удалить
/// созданное значение и записать ноль — разные вещи.
/// </summary>
public sealed class RegistrySnapshot
{
    public string TargetId { get; set; } = "";
    public bool Exists { get; set; }
    public string Kind { get; set; } = "String";
    public string Data { get; set; } = "";

    public RegistryTarget Resolve() => RegistryTarget.Parse(TargetId);
}

public static class RegistryIo
{
    private static RegistryKey BaseKey(RegHive hive)
        => hive == RegHive.CurrentUser ? Registry.CurrentUser : Registry.LocalMachine;

    public static (bool exists, object? value, RegistryValueKind kind) Read(RegistryTarget target)
    {
        using RegistryKey? key = BaseKey(target.Hive).OpenSubKey(target.Path, writable: false);
        if (key == null) return (false, null, RegistryValueKind.Unknown);

        object? value = key.GetValue(target.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value == null) return (false, null, RegistryValueKind.Unknown);

        RegistryValueKind kind = key.GetValueKind(target.Name);
        return (true, value, kind);
    }

    public static string? ReadString(RegistryTarget target)
    {
        var (exists, value, _) = Read(target);
        return exists ? Convert.ToString(value) : null;
    }

    public static int? ReadInt(RegistryTarget target)
    {
        var (exists, value, _) = Read(target);
        if (!exists || value == null) return null;
        try { return Convert.ToInt32(value); } catch { return null; }
    }

    public static bool TryWrite(RegistryTarget target, object value, RegistryValueKind kind, out string error)
    {
        error = "";
        try
        {
            using RegistryKey key = BaseKey(target.Hive).CreateSubKey(target.Path, writable: true)
                                    ?? throw new InvalidOperationException("не удалось открыть ключ");
            key.SetValue(target.Name, value, kind);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool TryDeleteValue(RegistryTarget target, out string error)
    {
        error = "";
        try
        {
            using RegistryKey? key = BaseKey(target.Hive).OpenSubKey(target.Path, writable: true);
            key?.DeleteValue(target.Name, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static RegistrySnapshot Capture(RegistryTarget target)
    {
        var (exists, value, kind) = Read(target);
        var snapshot = new RegistrySnapshot
        {
            TargetId = target.Id,
            Exists = exists,
            Kind = kind.ToString(),
            Data = exists ? Encode(value, kind) : ""
        };
        return snapshot;
    }

    public static bool Restore(RegistrySnapshot snapshot, out string error)
    {
        error = "";
        RegistryTarget target;
        try { target = snapshot.Resolve(); }
        catch (Exception ex) { error = ex.Message; return false; }

        if (!snapshot.Exists)
        {
            // Значения не было до изменения — значит откат обязан его удалить,
            // а не записать ноль или пустую строку.
            return TryDeleteValue(target, out error);
        }

        try
        {
            RegistryValueKind kind = Enum.TryParse(snapshot.Kind, out RegistryValueKind parsed)
                ? parsed
                : RegistryValueKind.String;

            object value = kind switch
            {
                RegistryValueKind.DWord => unchecked((int)Convert.ToUInt32(snapshot.Data, 10)),
                RegistryValueKind.QWord => unchecked((long)Convert.ToUInt64(snapshot.Data, 10)),
                RegistryValueKind.Binary => Convert.FromBase64String(snapshot.Data),
                RegistryValueKind.MultiString => snapshot.Data.Split('\n'),
                _ => snapshot.Data
            };

            return TryWrite(target, value, kind, out error);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static string Describe(RegistrySnapshot snapshot)
        => snapshot.Exists ? $"{snapshot.Kind}: {snapshot.Data}" : "значение отсутствовало";

    private static string Encode(object? value, RegistryValueKind kind)
    {
        if (value == null) return "";
        return kind switch
        {
            RegistryValueKind.DWord => Convert.ToUInt32(value).ToString(),
            RegistryValueKind.QWord => Convert.ToUInt64(value).ToString(),
            RegistryValueKind.Binary => Convert.ToBase64String((byte[])value),
            RegistryValueKind.MultiString => string.Join("\n", (string[])value),
            _ => Convert.ToString(value) ?? ""
        };
    }
}

/// <summary>
/// Строки вида «key=value;key2=value2;» (DirectXUserGlobalSettings)
/// и «~ TOKEN1 TOKEN2» (AppCompatFlags\Layers). В обоих случаях нельзя
/// перезаписывать строку целиком: там живут чужие настройки и токены.
/// </summary>
public static class TokenString
{
    public static string? GetToken(string? source, string key)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        foreach (string part in source.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            if (string.Equals(part[..eq].Trim(), key, StringComparison.OrdinalIgnoreCase))
                return part[(eq + 1)..].Trim();
        }
        return null;
    }

    public static string SetToken(string? source, string key, string value)
    {
        var parts = new System.Collections.Generic.List<string>();
        bool replaced = false;

        if (!string.IsNullOrWhiteSpace(source))
        {
            foreach (string raw in source.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = raw.IndexOf('=');
                if (eq > 0 && string.Equals(raw[..eq].Trim(), key, StringComparison.OrdinalIgnoreCase))
                {
                    if (!replaced)
                    {
                        parts.Add($"{key}={value}");
                        replaced = true;
                    }
                    continue;
                }
                parts.Add(raw.Trim());
            }
        }

        if (!replaced) parts.Add($"{key}={value}");
        return string.Join(";", parts) + ";";
    }

    public static string RemoveToken(string? source, string key)
    {
        if (string.IsNullOrWhiteSpace(source)) return "";
        var parts = new System.Collections.Generic.List<string>();
        foreach (string raw in source.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = raw.IndexOf('=');
            if (eq > 0 && string.Equals(raw[..eq].Trim(), key, StringComparison.OrdinalIgnoreCase))
                continue;
            parts.Add(raw.Trim());
        }
        return parts.Count == 0 ? "" : string.Join(";", parts) + ";";
    }

    public static bool HasLayerToken(string? source, string token)
        => !string.IsNullOrWhiteSpace(source)
           && source.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Any(t => string.Equals(t, token, StringComparison.OrdinalIgnoreCase));

    public static string AddLayerToken(string? source, string token)
    {
        var tokens = new System.Collections.Generic.List<string>();
        if (!string.IsNullOrWhiteSpace(source))
        {
            foreach (string raw in source.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!tokens.Contains(raw, StringComparer.OrdinalIgnoreCase))
                    tokens.Add(raw);
            }
        }

        if (!tokens.Contains("~")) tokens.Insert(0, "~");
        if (!tokens.Contains(token, StringComparer.OrdinalIgnoreCase)) tokens.Add(token);
        return string.Join(" ", tokens);
    }

    public static string RemoveLayerToken(string? source, string token)
    {
        var tokens = new System.Collections.Generic.List<string>();
        if (!string.IsNullOrWhiteSpace(source))
        {
            foreach (string raw in source.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.Equals(raw, token, StringComparison.OrdinalIgnoreCase)) continue;
                if (!tokens.Contains(raw, StringComparer.OrdinalIgnoreCase))
                    tokens.Add(raw);
            }
        }

        // Если остался только маркер «~», значение бессмысленно — удаляем его.
        if (tokens.Count <= 1) return "";
        return string.Join(" ", tokens);
    }
}
