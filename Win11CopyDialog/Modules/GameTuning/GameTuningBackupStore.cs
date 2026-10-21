using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Win11CopyDialog.Modules.GameTuning;

/// <summary>
/// Хранит исходные значения изменённых настроек. Нужен именно снимок,
/// а не «значение по умолчанию Windows»: у пользователя могли быть свои
/// настройки, и откат обязан вернуть их, а не чужой шаблон.
/// </summary>
public sealed class GameTuningBackupStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true
    };

    private readonly string _path;

    public GameTuningBackupStore(string path)
    {
        _path = path;
    }

    public string Path => _path;

    public List<TweakBackupEntry> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new List<TweakBackupEntry>();
            string json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<List<TweakBackupEntry>>(json, Options) ?? new List<TweakBackupEntry>();
        }
        catch
        {
            return new List<TweakBackupEntry>();
        }
    }

    public void Save(List<TweakBackupEntry> entries)
    {
        string? dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(entries, Options));
        File.Move(tmp, _path, overwrite: true);
    }

    public TweakBackupEntry? Find(string tweakId, string? gameExe)
    {
        string exe = gameExe ?? "";
        return Load().FirstOrDefault(e =>
            string.Equals(e.TweakId, tweakId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(e.GameExe, exe, StringComparison.OrdinalIgnoreCase));
    }

    public void Put(TweakBackupEntry entry)
    {
        var all = Load();
        all.RemoveAll(e =>
            string.Equals(e.TweakId, entry.TweakId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(e.GameExe, entry.GameExe, StringComparison.OrdinalIgnoreCase));
        all.Add(entry);
        Save(all);
    }

    public void Remove(string tweakId, string? gameExe)
    {
        var all = Load();
        int removed = all.RemoveAll(e =>
            string.Equals(e.TweakId, tweakId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(e.GameExe, gameExe ?? "", StringComparison.OrdinalIgnoreCase));
        if (removed > 0) Save(all);
    }

    public int Count => Load().Count;
}
