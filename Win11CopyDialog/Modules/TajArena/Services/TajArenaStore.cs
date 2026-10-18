using System.IO;
using System.Text.Json;
using Win11CopyDialog.Helpers;
using Win11CopyDialog.Modules.TajArena.Models;

namespace Win11CopyDialog.Modules.TajArena.Services;

/// <summary>
/// Состояние игровой категории, живущее между запусками программы.
/// </summary>
/// <remarks>
/// <para>Хранится три вещи: игры, добавленные пользователем вручную;
/// найденные автоматически записи, отмеченные скрытыми; и исполняемый
/// файл, который пользователь выбрал для каждой игры, когда автоматика
/// не смогла определиться.</para>
///
/// <para>Автопоиск каждый раз выполняется заново: он обязан отражать
/// актуальное состояние дисков, а не вчерашнее. Поэтому в файл не
/// попадают размеры и проценты — это измерения, а не предпочтения.</para>
/// </remarks>
public sealed class TajArenaState
{
    /// <summary>Версия формата файла. При несовместимом изменении — +1.</summary>
    public int Version { get; set; } = 1;

    public List<GameEntry> ManualGames { get; set; } = new();

    public List<string> HiddenPaths { get; set; } = new();

    /// <summary>Папка игры → путь выбранного пользователем exe.</summary>
    public Dictionary<string, string> ChosenExes { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Чтение и запись состояния TAJ ARENA в <see cref="AppPaths.TajArenaFile"/>.
/// </summary>
/// <remarks>
/// Все операции защищены: файл настроек не является гарантией работающей
/// программы. Повреждённый файл трактуется как отсутствующий (пустое
/// состояние), а не как повод уронить окно. Поверх повреждения программа
/// записывает пустое состояние при первом же сохранении, так что
/// «сломанный файл навсегда» не возникает.
/// </remarks>
public static class TajArenaStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        IgnoreReadOnlyProperties = true
    };

    public static TajArenaState Load()
    {
        var state = new TajArenaState();
        try
        {
            var path = AppPaths.TajArenaFile;
            if (!File.Exists(path)) return state;

            var loaded = JsonSerializer.Deserialize<TajArenaState>(
                File.ReadAllText(path), Options);
            if (loaded == null) return state;

            // Восстановление-откат на пустое состояние при некорректных
            // типах: пользователь потеряет отметки, но не получит краш.
            return new TajArenaState
            {
                Version = loaded.Version,
                ManualGames = loaded.ManualGames ?? new List<GameEntry>(),
                HiddenPaths = loaded.HiddenPaths ?? new List<string>(),
                ChosenExes = loaded.ChosenExes ??
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            };
        }
        catch (Exception)
        {
            // Файл повреждён или недоступен: работаем на пустом состоянии.
            return new TajArenaState();
        }
    }

    /// <summary>
    /// Сохраняет состояние. Возвращает true при успехе — вызывающий
    /// код обязан сообщить пользователю, когда сохранить не удалось,
    /// вместо того чтобы притвориться, что всё записалось.
    /// </summary>
    public static bool Save(TajArenaState state)
    {
        try
        {
            var path = AppPaths.TajArenaFile;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(state, Options));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
