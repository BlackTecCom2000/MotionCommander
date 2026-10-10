using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Win11CopyDialog.Modules.Navigation.Services;

public static class FavoritesManager
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MotionCommander",
        "favorites.json");

    private static readonly HashSet<string> _favorites = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded = false;

    public static event EventHandler? FavoritesChanged;

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (File.Exists(ConfigPath))
            {
                string json = File.ReadAllText(ConfigPath);
                var list = JsonSerializer.Deserialize<List<string>>(json);
                if (list != null)
                {
                    foreach (var p in list)
                    {
                        if (Directory.Exists(p)) _favorites.Add(p);
                    }
                }
            }
        }
        catch { }
    }

    private static void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            string json = JsonSerializer.Serialize(new List<string>(_favorites), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch { }
    }

    public static bool IsFavorite(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        EnsureLoaded();
        return _favorites.Contains(path.TrimEnd('\\', '/'));
    }

    public static void ToggleFavorite(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        EnsureLoaded();
        string clean = path.TrimEnd('\\', '/');
        if (_favorites.Contains(clean))
        {
            _favorites.Remove(clean);
        }
        else
        {
            _favorites.Add(clean);
        }
        Save();
        FavoritesChanged?.Invoke(null, EventArgs.Empty);
    }

    public static List<string> GetFavorites()
    {
        EnsureLoaded();
        return new List<string>(_favorites);
    }
}
