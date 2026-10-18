using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace Win11CopyDialog.Modules.TajArena.Models;

/// <summary>
/// Откуда игра попала в список.
/// </summary>
/// <remarks>
/// Источник не декоративный: по нему отличается поведение при
/// удалении из списка. Пользовательская запись действительно
/// стирается из хранилища, а найденная автоматически лишь
/// скрывается — иначе при следующем поиске она вернулась бы,
/// и это выглядело бы как «удаление не работает».
/// </remarks>
public enum GamePlatform
{
    Steam,
    EpicGames,
    RiotGames,
    GoG,
    Manual
}

/// <summary>
/// Одна игра в списке TAJ ARENA.
/// </summary>
/// <remarks>
/// <para>Все поля заполняются только из реальных источников:
/// реестра, файлов библиотек Steam или папок, выбранных
/// пользователем через диалог. Ничего не дописывается «для
/// красоты»: если платформа не опознаётся — записи не будет.</para>
///
/// <para>Класс реализует <see cref="INotifyPropertyChanged"/>,
/// потому что размеры и проценты приходят после того, как строка
/// уже показана: измерения выполняются в фоне, и список обязан
/// обновляться построчно, а не перестраиваться целиком
/// (полная перестройка сбрасывала бы выбор пользователя).</para>
/// </remarks>
public sealed class GameEntry : INotifyPropertyChanged
{
    /// <summary>Название, как его сообщает источник (реестр, папка).</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Корневая папка установки, без завершающего разделителя.</summary>
    public string InstallPath { get; set; } = "";

    /// <summary>Платформа, по которой игра была обнаружена.</summary>
    public GamePlatform Platform { get; set; } = GamePlatform.Manual;

    private string? _mainExePath;
    /// <summary>
    /// Исполняемый файл, который запускается по кнопке «Играть».
    /// Null, пока не выбран автоматически или вручную.
    /// </summary>
    public string? MainExePath
    {
        get => _mainExePath;
        set
        {
            if (_mainExePath == value) return;
            _mainExePath = value;
            Raise(nameof(MainExeName));
            Raise(nameof(LaunchBadgeBrush));
            Raise(nameof(LaunchBadgeForeground));
            Raise(nameof(LaunchBadgeText));
        }
    }

    /// <summary>Кандидаты на главную программу.</summary>
    public List<string> CandidateExePaths { get; set; } = new();

    private long? _sizeBytes;
    /// <summary>
    /// Реальный объём папки в байтах. Null — ещё не измерен.
    /// Устаревшие числа не хранятся: размер считается заново при
    /// каждом открытии окна и каждом поиске.
    /// </summary>
    public long? SizeBytes
    {
        get => _sizeBytes;
        set
        {
            if (_sizeBytes == value) return;
            _sizeBytes = value;
            Raise(nameof(SizeFormatted));
        }
    }

    private double? _percentOfDisk;
    /// <summary>
    /// Доля этой игры от объёма диска, на котором установлена, 0–100.
    /// Null — диск недоступен или измерение не выполнено.
    /// </summary>
    public double? PercentOfDisk
    {
        get => _percentOfDisk;
        set
        {
            if (_percentOfDisk == value) return;
            _percentOfDisk = value;
            Raise(nameof(PercentFormatted));
        }
    }

    private string _notes = "";
    /// <summary>Текст последних пояснений: что не измерено и почему.</summary>
    public string Notes
    {
        get => _notes;
        set
        {
            if (_notes == value) return;
            _notes = value;
            Raise(nameof(Notes));
        }
    }

    /// <summary>Скрыта ли игра из списка (для найденных автоматически).</summary>
    public bool Hidden { get; set; }

    public string PlatformName => Platform switch
    {
        GamePlatform.Steam => "Steam",
        GamePlatform.EpicGames => "Epic Games",
        GamePlatform.RiotGames => "Riot Games",
        GamePlatform.GoG => "GOG",
        _ => "Папка (вручную)"
    };

    /// <summary>Размер, отформатированный по правилам общих хелперов.</summary>
    public string SizeFormatted => SizeBytes is long s && s >= 0
        ? Helpers.Formatters.Bytes(s)
        : "—";

    /// <summary>Доля диска в процентах, готовая к отображению.</summary>
    public string PercentFormatted => PercentOfDisk is double p && !double.IsNaN(p) && !double.IsInfinity(p)
        ? p.ToString("F1") + "%"
        : "—";

    /// <summary>Имя исполняемого файла, или пояснение, что файл не выбран.</summary>
    public string MainExeName
    {
        get
        {
            if (string.IsNullOrEmpty(MainExePath)) return "не выбран";
            try { return Path.GetFileName(MainExePath); }
            catch { return "не выбран"; }
        }
    }

    /// <summary>
    /// Кисти плашек создаются один раз и замораживаются: биндинг WPF
    /// вызывает свойства дважды на строку при каждой перерисовке, и
    /// новая кисть при каждом обращении блокировала бы UI-поток.
    /// Паттерн взят из DriverDeviceInfo (окно инспектора драйверов).
    /// </summary>
    private static readonly Brush _readyBadge = Frozen(Color.FromArgb(40, 0, 230, 118));
    private static readonly Brush _unknownBadge = Frozen(Color.FromArgb(40, 255, 179, 0));
    private static readonly Brush _readyFg = Frozen(Color.FromRgb(0, 230, 118));
    private static readonly Brush _unknownFg = Frozen(Color.FromRgb(255, 179, 0));

    private static SolidColorBrush Frozen(Color color)
    {
        var b = new SolidColorBrush(color);
        b.Freeze();
        return b;
    }

    /// <summary>Цвет плашки «готово к запуску» / «файл не выбран».</summary>
    public Brush LaunchBadgeBrush =>
        string.IsNullOrEmpty(MainExePath) ? _unknownBadge : _readyBadge;

    public Brush LaunchBadgeForeground =>
        string.IsNullOrEmpty(MainExePath) ? _unknownFg : _readyFg;

    public string LaunchBadgeText => string.IsNullOrEmpty(MainExePath)
        ? "файл не выбран"
        : "готово";

    /// <summary>Корень диска, на котором установлена игра («C:\») — для расчёта доли.</summary>
    public string? DriveRoot
    {
        get
        {
            try
            {
                var root = Path.GetPathRoot(InstallPath);
                return string.IsNullOrWhiteSpace(root) ? null : root;
            }
            catch
            {
                return null;
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string name = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
