using System;
using System.Collections.Generic;

namespace Win11CopyDialog.Modules.GameTuning;

public enum TweakGroup
{
    Power,
    Windows,
    Gpu,
    Input,
    Memory,
    PerGame
}

public enum TweakScope
{
    System,
    PerGame
}

/// <summary>
/// Что именно измеряет или меняет твик. Это не украшение: интерфейс не имеет
/// права писать «+FPS» там, где меняется задержка ввода или только память.
/// </summary>
public enum TweakEffectKind
{
    Fps,
    FrameTime,
    InputLatency,
    Memory,
    Compatibility
}

public enum TweakRisk
{
    Safe,
    Moderate,
    Advanced
}

public enum TweakStatus
{
    Applied,
    NotApplied,
    Unavailable,
    Unknown
}

public sealed class TweakStatusInfo
{
    public TweakStatus Status { get; init; } = TweakStatus.Unknown;
    public string Current { get; init; } = "";
    public string Target { get; init; } = "";
    public string Detail { get; init; } = "";

    public bool CanApply => Status is TweakStatus.NotApplied or TweakStatus.Unknown;

    public string StatusText => Status switch
    {
        TweakStatus.Applied => "применено",
        TweakStatus.NotApplied => "не применено",
        TweakStatus.Unavailable => "недоступно",
        _ => "неизвестно"
    };
}

public sealed class TweakOperationResult
{
    public bool Ok { get; init; }
    public string Message { get; init; } = "";

    public static TweakOperationResult Success(string message) => new() { Ok = true, Message = message };
    public static TweakOperationResult Fail(string message) => new() { Ok = false, Message = message };
}

public sealed class GameTweak
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public TweakGroup Group { get; init; } = TweakGroup.Windows;
    public TweakScope Scope { get; init; } = TweakScope.System;
    public TweakEffectKind Effect { get; init; } = TweakEffectKind.FrameTime;
    public string EffectText { get; init; } = "";
    public string Mechanism { get; init; } = "";
    public string Limitation { get; init; } = "";
    public bool RequiresAdmin { get; init; }
    public bool RequiresReboot { get; init; }
    public TweakRisk Risk { get; init; } = TweakRisk.Safe;
    public bool Recommended { get; init; }

    /// <summary>Разовое действие, а не переключатель состояния (сброс standby-кэша).</summary>
    public bool IsAction { get; init; }

    public Func<string?, IEnumerable<RegistryTarget>> SnapshotTargetsFor { get; init; } = _ => Array.Empty<RegistryTarget>();
    public Func<string?, TweakStatusInfo> ReadStatus { get; init; } = _ => new TweakStatusInfo();
    public Func<string?, TweakOperationResult> Apply { get; init; } = _ => TweakOperationResult.Fail("не реализовано");
    public Func<string?, TweakOperationResult> Revert { get; init; } = _ => TweakOperationResult.Fail("не реализовано");

    public string GroupTitle => Group switch
    {
        TweakGroup.Power => "Питание и планировщик",
        TweakGroup.Windows => "Windows и фон",
        TweakGroup.Gpu => "GPU и кадры",
        TweakGroup.Input => "Ввод",
        TweakGroup.Memory => "Память",
        TweakGroup.PerGame => "Для выбранной игры",
        _ => "Прочее"
    };

    public string EffectTitle => Effect switch
    {
        TweakEffectKind.Fps => "FPS",
        TweakEffectKind.FrameTime => "Стабильность кадров",
        TweakEffectKind.InputLatency => "Задержка ввода",
        TweakEffectKind.Memory => "Память",
        TweakEffectKind.Compatibility => "Совместимость",
        _ => "Эффект"
    };

    public string RiskTitle => Risk switch
    {
        TweakRisk.Safe => "безопасно",
        TweakRisk.Moderate => "умеренный риск",
        _ => "для опытных"
    };
}

/// <summary>
/// Рекомендация с измеренным основанием. Поле Basis заполняется фактическим
/// значением с этой машины, а не общим текстом «так быстрее».
/// </summary>
public sealed class GameRecommendation
{
    public string TweakId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Basis { get; init; } = "";
    public string Action { get; init; } = "";
}

public sealed class TweakBackupEntry
{
    public string TweakId { get; set; } = "";
    public string GameExe { get; set; } = "";
    public DateTime AppliedUtc { get; set; } = DateTime.UtcNow;
    public List<RegistrySnapshot> Values { get; set; } = new();

    /// <summary>Активная схема питания до изменения (GUID).</summary>
    public string PowerSchemeGuid { get; set; } = "";
    public bool HadPowerScheme { get; set; }
}
