using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Win11CopyDialog.Modules.GameTuning;

/// <summary>
/// Каталог настроек, которые действительно меняются этой программой.
///
/// Правила каталога:
/// <list type="bullet">
/// <item>каждый твик умеет прочитать своё фактическое состояние и проверить
/// запись после применения (ReadStatus вызывается повторно);</item>
/// <item>эффект подписан тем, что он реально меняет, а не «+FPS» на всякий
/// случай: ускорение указателя не повышает частоту кадров;</item>
/// <item>ограничение указано рядом с эффектом — где прироста не будет.</item>
/// </list>
/// </summary>
public static class GameTweakCatalog
{
    public const string IdPower = "power.high_performance";
    public const string IdGameMode = "game.mode";
    public const string IdDvr = "game.dvr_off";
    public const string IdHags = "gpu.hags";
    public const string IdWindowed = "gpu.windowed_optimizations";
    public const string IdVisualFx = "system.visual_fx_perf";
    public const string IdResponsiveness = "system.responsiveness";
    public const string IdMouse = "input.mouse_accel_off";
    public const string IdStandbyTrim = "memory.standby_trim";
    public const string IdGpuPreference = "game.gpu_preference";
    public const string IdFullscreenOptimizations = "game.fullscreen_optimizations";

    private static readonly RegistryTarget GameModeAuto =
        RegistryTarget.Hkcu(@"Software\Microsoft\GameBar", "AutoGameModeEnabled");

    private static readonly RegistryTarget GameModeAllow =
        RegistryTarget.Hkcu(@"Software\Microsoft\GameBar", "AllowAutoGameMode");

    private static readonly RegistryTarget DvrStore =
        RegistryTarget.Hkcu(@"System\GameConfigStore", "GameDVR_Enabled");

    private static readonly RegistryTarget DvrCapture =
        RegistryTarget.Hkcu(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled");

    private static readonly RegistryTarget Hags =
        RegistryTarget.Hklm(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode");

    private static readonly RegistryTarget GlobalGpuPrefs =
        RegistryTarget.Hkcu(@"Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings");

    private static readonly RegistryTarget VisualFx =
        RegistryTarget.Hkcu(@"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting");

    private static readonly RegistryTarget SystemResponsiveness =
        RegistryTarget.Hklm(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness");

    private static readonly RegistryTarget NetworkThrottling =
        RegistryTarget.Hklm(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex");

    private static readonly RegistryTarget MouseSpeed =
        RegistryTarget.Hkcu(@"Control Panel\Mouse", "MouseSpeed");

    private static readonly RegistryTarget MouseThreshold1 =
        RegistryTarget.Hkcu(@"Control Panel\Mouse", "MouseThreshold1");

    private static readonly RegistryTarget MouseThreshold2 =
        RegistryTarget.Hkcu(@"Control Panel\Mouse", "MouseThreshold2");

    private static readonly RegistryTarget GpuPreferences =
        RegistryTarget.Hkcu(@"Software\Microsoft\DirectX\UserGpuPreferences", "");

    private static readonly RegistryTarget Layers =
        RegistryTarget.Hkcu(@"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers", "");

    public static IReadOnlyList<GameTweak> All { get; } = Build();

    private static List<GameTweak> Build() => new()
    {
        PowerTweak(),
        GameModeTweak(),
        DvrTweak(),
        VisualFxTweak(),
        WindowedOptimizationsTweak(),
        MouseTweak(),
        HagsTweak(),
        ResponsivenessTweak(),
        StandbyTrimTweak(),
        GpuPreferenceTweak(),
        FullscreenOptimizationsTweak()
    };

    // ───────────────────────── питание ─────────────────────────

    private static GameTweak PowerTweak() => new()
    {
        Id = IdPower,
        Title = "Схема питания «Высокая производительность»",
        Group = TweakGroup.Power,
        Effect = TweakEffectKind.FrameTime,
        EffectText = "меньше просадок 1% low, частоты не сбрасываются",
        Mechanism = "Схема запрещает переходы в глубокий сон ядер и снижение частоты во время игры.",
        Limitation = "На настольном ПК с хорошим охлаждением средний FPS обычно не меняется — эффект виден в 1% low и на ноутбуках.",
        Risk = TweakRisk.Safe,
        Recommended = true,
        SnapshotTargetsFor = _ => Array.Empty<RegistryTarget>(),
        ReadStatus = _ => ReadPowerStatus(),
        Apply = _ => ActivatePowerScheme(PowerScheme.HighPerformance),
        Revert = _ => ActivatePowerScheme(PowerScheme.Balanced)
    };

    private static TweakStatusInfo ReadPowerStatus()
    {
        (string guid, string name) = PowerScheme.GetActive();
        if (string.IsNullOrEmpty(guid))
            return new TweakStatusInfo
            {
                Status = TweakStatus.Unavailable,
                Detail = "powercfg не вернул активную схему" +
                         (string.IsNullOrWhiteSpace(name) ? "" : ": " + name)
            };

        bool applied = PowerScheme.IsHighPerformance(guid);
        return new TweakStatusInfo
        {
            Status = applied ? TweakStatus.Applied : TweakStatus.NotApplied,
            Current = string.IsNullOrWhiteSpace(name) ? PowerScheme.FriendlyName(guid) : name,
            Target = "Высокая производительность",
            Detail = applied ? "Активна схема высокой производительности" : "Активна другая схема"
        };
    }

    private static TweakOperationResult ActivatePowerScheme(string guid)
    {
        if (!PowerScheme.TryActivate(guid, out string error))
            return TweakOperationResult.Fail("powercfg: " + error);

        TweakStatusInfo status = ReadPowerStatus();
        return status.Status == TweakStatus.Applied
            ? TweakOperationResult.Success("активна схема «" + PowerScheme.FriendlyName(guid) + "» (проверено)")
            : TweakOperationResult.Fail("схема не подтвердилась: " + status.Detail);
    }

    // ───────────────────────── Game Mode ─────────────────────────

    private static GameTweak GameModeTweak() => new()
    {
        Id = IdGameMode,
        Title = "Режим игры Windows",
        Group = TweakGroup.Windows,
        Effect = TweakEffectKind.FrameTime,
        EffectText = "планировщик отдаёт приоритет игре",
        Mechanism = "Game Mode снижает вмешательство фоновых задач в потоки игры.",
        Limitation = "В Windows 11 режим обычно уже включён; тогда изменения состояния не будет.",
        Risk = TweakRisk.Safe,
        Recommended = true,
        SnapshotTargetsFor = _ => new[] { GameModeAuto, GameModeAllow },
        ReadStatus = _ => ReadGameModeStatus(),
        Apply = _ => WriteGameMode(true),
        Revert = _ => WriteGameMode(false)
    };

    private static TweakStatusInfo ReadGameModeStatus()
    {
        int? auto = RegistryIo.ReadInt(GameModeAuto);
        int? allow = RegistryIo.ReadInt(GameModeAllow);
        bool applied = auto == 1 && allow == 1;

        return new TweakStatusInfo
        {
            Status = applied ? TweakStatus.Applied : TweakStatus.NotApplied,
            Current = $"AutoGameModeEnabled={auto?.ToString() ?? "не задано"}, AllowAutoGameMode={allow?.ToString() ?? "не задано"}",
            Target = "оба значения = 1",
            Detail = applied ? "Режим игры включён" : "Режим игры выключен или не настроен"
        };
    }

    private static TweakOperationResult WriteGameMode(bool enabled)
    {
        int value = enabled ? 1 : 0;
        if (!RegistryIo.TryWrite(GameModeAuto, value, RegistryValueKind.DWord, out string e1))
            return TweakOperationResult.Fail("AutoGameModeEnabled: " + e1);
        if (!RegistryIo.TryWrite(GameModeAllow, value, RegistryValueKind.DWord, out string e2))
            return TweakOperationResult.Fail("AllowAutoGameMode: " + e2);

        TweakStatusInfo status = ReadGameModeStatus();
        bool expected = enabled ? status.Status == TweakStatus.Applied : status.Status != TweakStatus.Applied;
        return expected
            ? TweakOperationResult.Success(enabled ? "режим игры включён (проверено)" : "режим игры выключен (проверено)")
            : TweakOperationResult.Fail("состояние не подтвердилось: " + status.Detail);
    }

    // ───────────────────────── Game DVR ─────────────────────────

    private static GameTweak DvrTweak() => new()
    {
        Id = IdDvr,
        Title = "Фоновая запись Game DVR выключена",
        Group = TweakGroup.Windows,
        Effect = TweakEffectKind.Fps,
        EffectText = "убирает постоянную запись экрана в фоне",
        Mechanism = "Game DVR кодирует и пишет видео во время игры, забирая GPU, CPU и диск.",
        Limitation = "Если запись уже была выключена, прироста не будет. Клипы Game Bar перестанут записываться.",
        Risk = TweakRisk.Safe,
        Recommended = true,
        SnapshotTargetsFor = _ => new[] { DvrStore, DvrCapture },
        ReadStatus = _ => ReadDvrStatus(),
        Apply = _ => WriteDvr(true),
        Revert = _ => WriteDvr(false)
    };

    private static TweakStatusInfo ReadDvrStatus()
    {
        int? store = RegistryIo.ReadInt(DvrStore);
        int? capture = RegistryIo.ReadInt(DvrCapture);
        bool applied = store == 0 && capture == 0;

        return new TweakStatusInfo
        {
            Status = applied ? TweakStatus.Applied : TweakStatus.NotApplied,
            Current = $"GameDVR_Enabled={store?.ToString() ?? "не задано"}, AppCaptureEnabled={capture?.ToString() ?? "не задано"}",
            Target = "оба значения = 0",
            Detail = applied ? "фоновая запись выключена" : "фоновая запись включена или не настроена"
        };
    }

    private static TweakOperationResult WriteDvr(bool disabled)
    {
        int value = disabled ? 0 : 1;
        if (!RegistryIo.TryWrite(DvrStore, value, RegistryValueKind.DWord, out string e1))
            return TweakOperationResult.Fail("GameDVR_Enabled: " + e1);
        if (!RegistryIo.TryWrite(DvrCapture, value, RegistryValueKind.DWord, out string e2))
            return TweakOperationResult.Fail("AppCaptureEnabled: " + e2);

        TweakStatusInfo status = ReadDvrStatus();
        bool ok = disabled ? status.Status == TweakStatus.Applied : status.Status != TweakStatus.Applied;
        return ok
            ? TweakOperationResult.Success(disabled ? "фоновая запись выключена (проверено)" : "фоновая запись включена (проверено)")
            : TweakOperationResult.Fail("состояние не подтвердилось: " + status.Detail);
    }

    // ───────────────────────── визуальные эффекты ─────────────────────────

    private static GameTweak VisualFxTweak() => new()
    {
        Id = IdVisualFx,
        Title = "Визуальные эффекты Windows: приоритет производительности",
        Group = TweakGroup.Windows,
        Effect = TweakEffectKind.Fps,
        EffectText = "освобождает GPU/CPU от анимаций оболочки",
        Mechanism = "Отключает тени, анимации и прозрачность окон, которые рисует система.",
        Limitation = "На мощном ПК эффект близок к нулю. Часть оформления меняется после входа в систему.",
        Risk = TweakRisk.Safe,
        Recommended = true,
        SnapshotTargetsFor = _ => new[] { VisualFx },
        ReadStatus = _ => ReadVisualFxStatus(),
        Apply = _ => WriteVisualFx(true),
        Revert = _ => WriteVisualFx(false)
    };

    private static TweakStatusInfo ReadVisualFxStatus()
    {
        int? value = RegistryIo.ReadInt(VisualFx);
        bool applied = value == 2;
        return new TweakStatusInfo
        {
            Status = applied ? TweakStatus.Applied : TweakStatus.NotApplied,
            Current = value?.ToString() ?? "не задано",
            Target = "2 (лучшая производительность)",
            Detail = applied ? "включён приоритет производительности" : "действует обычное оформление"
        };
    }

    private static TweakOperationResult WriteVisualFx(bool performance)
    {
        if (!RegistryIo.TryWrite(VisualFx, performance ? 2 : 0, RegistryValueKind.DWord, out string error))
            return TweakOperationResult.Fail("VisualFXSetting: " + error);

        TweakStatusInfo status = ReadVisualFxStatus();
        bool ok = performance ? status.Status == TweakStatus.Applied : status.Status != TweakStatus.Applied;
        return ok
            ? TweakOperationResult.Success(performance ? "приоритет производительности включён (проверено)" : "оформление возвращено (проверено)")
            : TweakOperationResult.Fail("состояние не подтвердилось: " + status.Detail);
    }

    // ───────────────────────── окна без рамки ─────────────────────────

    private static GameTweak WindowedOptimizationsTweak() => new()
    {
        Id = IdWindowed,
        Title = "Оптимизации для оконных игр (Windows 11)",
        Group = TweakGroup.Gpu,
        Effect = TweakEffectKind.FrameTime,
        EffectText = "ниже задержка в оконном и безрамочном режиме",
        Mechanism = "Windows 11 22H2+ включает независимое управление кадрами для окон без рамки (SwapEffectUpgrade).",
        Limitation = "Нужна Windows 11 22H2 или новее. В эксклюзивном полноэкранном режиме не влияет.",
        Risk = TweakRisk.Safe,
        Recommended = true,
        SnapshotTargetsFor = _ => new[] { GlobalGpuPrefs },
        ReadStatus = _ => ReadWindowedStatus(),
        Apply = _ => WriteWindowed(true),
        Revert = _ => WriteWindowed(false)
    };

    private static bool WindowedOptimizationsSupported
        => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);

    private static TweakStatusInfo ReadWindowedStatus()
    {
        if (!WindowedOptimizationsSupported)
        {
            return new TweakStatusInfo
            {
                Status = TweakStatus.Unavailable,
                Detail = "нужна Windows 11 22H2 (сборка 22621) или новее"
            };
        }

        string? current = RegistryIo.ReadString(GlobalGpuPrefs);
        string? token = TokenString.GetToken(current, "SwapEffectUpgradeEnable");
        bool applied = token == "1";

        return new TweakStatusInfo
        {
            Status = applied ? TweakStatus.Applied : TweakStatus.NotApplied,
            Current = token == null ? "SwapEffectUpgradeEnable не задан" : "SwapEffectUpgradeEnable=" + token,
            Target = "SwapEffectUpgradeEnable=1",
            Detail = applied ? "оптимизации оконных игр включены" : "оптимизации оконных игр выключены"
        };
    }

    private static TweakOperationResult WriteWindowed(bool enabled)
    {
        if (!WindowedOptimizationsSupported)
            return TweakOperationResult.Fail("нужна Windows 11 22H2 или новее");

        string? current = RegistryIo.ReadString(GlobalGpuPrefs);
        string updated = enabled
            ? TokenString.SetToken(current, "SwapEffectUpgradeEnable", "1")
            : TokenString.RemoveToken(current, "SwapEffectUpgradeEnable");

        if (string.IsNullOrWhiteSpace(updated))
        {
            RegistryIo.TryDeleteValue(GlobalGpuPrefs, out _);
        }
        else if (!RegistryIo.TryWrite(GlobalGpuPrefs, updated, RegistryValueKind.String, out string error))
        {
            return TweakOperationResult.Fail("DirectXUserGlobalSettings: " + error);
        }

        TweakStatusInfo status = ReadWindowedStatus();
        bool ok = enabled ? status.Status == TweakStatus.Applied : status.Status != TweakStatus.Applied;
        return ok
            ? TweakOperationResult.Success(enabled
                ? "оптимизации оконных игр включены (проверено, остальные параметры сохранены)"
                : "оптимизации оконных игр выключены (проверено, остальные параметры сохранены)")
            : TweakOperationResult.Fail("состояние не подтвердилось: " + status.Detail);
    }

    // ───────────────────────── ускорение мыши ─────────────────────────

    private static GameTweak MouseTweak() => new()
    {
        Id = IdMouse,
        Title = "Ускорение указателя мыши выключено",
        Group = TweakGroup.Input,
        Effect = TweakEffectKind.InputLatency,
        EffectText = "одинаковое движение при любой скорости",
        Mechanism = "Отключает системное ускорение: перемещение мыши становится линейным.",
        Limitation = "На FPS не влияет. Привычное ускорение пропадёт — для рабочих задач это может быть неудобно.",
        Risk = TweakRisk.Safe,
        Recommended = true,
        SnapshotTargetsFor = _ => new[] { MouseSpeed, MouseThreshold1, MouseThreshold2 },
        ReadStatus = _ => ReadMouseStatus(),
        Apply = _ => WriteMouse(true),
        Revert = _ => WriteMouse(false)
    };

    private static TweakStatusInfo ReadMouseStatus()
    {
        string? speed = RegistryIo.ReadString(MouseSpeed);
        string? t1 = RegistryIo.ReadString(MouseThreshold1);
        string? t2 = RegistryIo.ReadString(MouseThreshold2);
        bool applied = speed == "0" && t1 == "0" && t2 == "0";

        return new TweakStatusInfo
        {
            Status = applied ? TweakStatus.Applied : TweakStatus.NotApplied,
            Current = $"MouseSpeed={speed ?? "не задано"}, пороги={t1 ?? "н/д"}/{t2 ?? "н/д"}",
            Target = "MouseSpeed=0, MouseThreshold1=0, MouseThreshold2=0",
            Detail = applied ? "ускорение выключено" : "системное ускорение включено"
        };
    }

    private static TweakOperationResult WriteMouse(bool disabled)
    {
        string value = disabled ? "0" : "1";
        foreach (RegistryTarget target in new[] { MouseSpeed, MouseThreshold1, MouseThreshold2 })
        {
            if (!RegistryIo.TryWrite(target, value, RegistryValueKind.String, out string error))
                return TweakOperationResult.Fail(target.Name + ": " + error);
        }

        TweakStatusInfo status = ReadMouseStatus();
        bool ok = disabled ? status.Status == TweakStatus.Applied : status.Status != TweakStatus.Applied;
        return ok
            ? TweakOperationResult.Success(disabled ? "ускорение мыши выключено (проверено)" : "ускорение мыши включено (проверено)")
            : TweakOperationResult.Fail("состояние не подтвердилось: " + status.Detail);
    }

    // ───────────────────────── HAGS ─────────────────────────

    private static GameTweak HagsTweak() => new()
    {
        Id = IdHags,
        Title = "Аппаратное планирование GPU (HAGS)",
        Group = TweakGroup.Gpu,
        Effect = TweakEffectKind.FrameTime,
        EffectText = "ниже задержка вывода кадров на поддерживаемых GPU",
        Mechanism = "Планирование очереди GPU переносится в драйвер и видеопамять.",
        Limitation = "Требует прав администратора и перезагрузки. На многих системах средний FPS не меняется; на части конфигураций эффект отрицательный — твик не входит в рекомендуемые.",
        RequiresAdmin = true,
        RequiresReboot = true,
        Risk = TweakRisk.Advanced,
        Recommended = false,
        SnapshotTargetsFor = _ => new[] { Hags },
        ReadStatus = _ => ReadHagsStatus(),
        Apply = _ => WriteHags(2),
        Revert = _ => WriteHags(1)
    };

    private static TweakStatusInfo ReadHagsStatus()
    {
        int? value = RegistryIo.ReadInt(Hags);
        bool applied = value == 2;
        return new TweakStatusInfo
        {
            Status = applied ? TweakStatus.Applied : TweakStatus.NotApplied,
            Current = value?.ToString() ?? "не задано",
            Target = "2 (включено)",
            Detail = applied
                ? "HAGS включён (вступает в силу после перезагрузки)"
                : "HAGS выключен (значение 1 или не задано)"
        };
    }

    private static TweakOperationResult WriteHags(int value)
    {
        if (!RegistryIo.TryWrite(Hags, value, RegistryValueKind.DWord, out string error))
            return TweakOperationResult.Fail("HwSchMode: " + error);

        TweakStatusInfo status = ReadHagsStatus();
        bool ok = value == 2 ? status.Status == TweakStatus.Applied : status.Status != TweakStatus.Applied;
        return ok
            ? TweakOperationResult.Success(value == 2
                ? "HAGS включён (проверено). Изменение вступит в силу после перезагрузки"
                : "HAGS выключен (проверено). Изменение вступит в силу после перезагрузки")
            : TweakOperationResult.Fail("состояние не подтвердилось: " + status.Detail);
    }

    // ───────────────────────── отзывчивость мультимедиа ─────────────────────────

    private static GameTweak ResponsivenessTweak() => new()
    {
        Id = IdResponsiveness,
        Title = "Приоритет мультимедиа и снятие сетевого троттлинга",
        Group = TweakGroup.Windows,
        Effect = TweakEffectKind.FrameTime,
        EffectText = "меньше квантов планировщика фоновым задачам",
        Mechanism = "SystemResponsiveness=10 и NetworkThrottlingIndex=0xFFFFFFFF уменьшают троттлинг мультимедиа и сетевых пакетов.",
        Limitation = "Требует прав администратора. На части систем влияет на сетевую производительность; прирост FPS не гарантирован.",
        RequiresAdmin = true,
        Risk = TweakRisk.Moderate,
        Recommended = false,
        SnapshotTargetsFor = _ => new[] { SystemResponsiveness, NetworkThrottling },
        ReadStatus = _ => ReadResponsivenessStatus(),
        Apply = _ => WriteResponsiveness(true),
        Revert = _ => WriteResponsiveness(false)
    };

    private static TweakStatusInfo ReadResponsivenessStatus()
    {
        int? responsiveness = RegistryIo.ReadInt(SystemResponsiveness);
        int? throttling = RegistryIo.ReadInt(NetworkThrottling);
        bool applied = responsiveness == 10
                       && throttling.HasValue
                       && unchecked((uint)throttling.Value) == 0xFFFFFFFFu;

        return new TweakStatusInfo
        {
            Status = applied ? TweakStatus.Applied : TweakStatus.NotApplied,
            Current = $"SystemResponsiveness={responsiveness?.ToString() ?? "не задано"}, NetworkThrottlingIndex={(throttling.HasValue ? "0x" + unchecked((uint)throttling.Value).ToString("X8") : "не задано")}",
            Target = "SystemResponsiveness=10, NetworkThrottlingIndex=0xFFFFFFFF",
            Detail = applied ? "параметры применены" : "действуют значения Windows"
        };
    }

    private static TweakOperationResult WriteResponsiveness(bool gaming)
    {
        int responsiveness = gaming ? 10 : 20;
        int throttling = gaming ? unchecked((int)0xFFFFFFFF) : 10;

        if (!RegistryIo.TryWrite(SystemResponsiveness, responsiveness, RegistryValueKind.DWord, out string e1))
            return TweakOperationResult.Fail("SystemResponsiveness: " + e1);
        if (!RegistryIo.TryWrite(NetworkThrottling, throttling, RegistryValueKind.DWord, out string e2))
            return TweakOperationResult.Fail("NetworkThrottlingIndex: " + e2);

        TweakStatusInfo status = ReadResponsivenessStatus();
        bool ok = gaming ? status.Status == TweakStatus.Applied : status.Status != TweakStatus.Applied;
        return ok
            ? TweakOperationResult.Success(gaming ? "параметры мультимедиа применены (проверено)" : "значения Windows возвращены (проверено)")
            : TweakOperationResult.Fail("состояние не подтвердилось: " + status.Detail);
    }

    // ───────────────────────── standby-кэш ─────────────────────────

    private const int SystemMemoryListInformation = 0x0050;
    private const int MemoryPurgeStandbyList = 4;

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int infoClass, IntPtr info, int length);

    private static GameTweak StandbyTrimTweak() => new()
    {
        Id = IdStandbyTrim,
        Title = "Очистка standby-кэша памяти",
        Group = TweakGroup.Memory,
        Effect = TweakEffectKind.Memory,
        EffectText = "освобождает зарезервированную память",
        Mechanism = "Сбрасывает список ожидания (standby list): память из кэша возвращается свободному пулу.",
        Limitation = "Разовое действие, состояние не сохраняется. FPS напрямую не повышает; кэш нужен системе и заполнится снова. Требует прав администратора.",
        RequiresAdmin = true,
        Risk = TweakRisk.Moderate,
        Recommended = false,
        IsAction = true,
        SnapshotTargetsFor = _ => Array.Empty<RegistryTarget>(),
        ReadStatus = _ => new TweakStatusInfo
        {
            Status = TweakStatus.NotApplied,
            Current = "разовое действие",
            Target = "сброс standby-списка",
            Detail = "Состояние не хранится: действие можно выполнить повторно"
        },
        Apply = _ => RunStandbyTrim(),
        Revert = _ => TweakOperationResult.Fail("разовое действие не требует отката")
    };

    private static TweakOperationResult RunStandbyTrim()
    {
        try
        {
            int value = MemoryPurgeStandbyList;
            IntPtr buffer = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                Marshal.WriteInt32(buffer, value);
                int status = NtSetSystemInformation(SystemMemoryListInformation, buffer, sizeof(int));
                if (status != 0)
                    return TweakOperationResult.Fail($"NtSetSystemInformation вернул 0x{status:X8}");

                return TweakOperationResult.Success("standby-список сброшен (вызов принят системой)");
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex)
        {
            return TweakOperationResult.Fail("ошибка сброса памяти: " + ex.Message);
        }
    }

    // ───────────────────────── настройки выбранной игры ─────────────────────────

    private static GameTweak GpuPreferenceTweak() => new()
    {
        Id = IdGpuPreference,
        Title = "Высокая производительность GPU для игры",
        Group = TweakGroup.PerGame,
        Scope = TweakScope.PerGame,
        Effect = TweakEffectKind.Fps,
        EffectText = "игра запускается на дискретной видеокарте",
        Mechanism = "Windows хранит предпочтение GPU для каждого exe; «высокая производительность» выбирает дискретный адаптер.",
        Limitation = "Влияет только на ноутбуках и ПК с двумя GPU. На системе с одной видеокартой состояние уже верное, и прироста не будет.",
        Risk = TweakRisk.Safe,
        Recommended = true,
        SnapshotTargetsFor = exe => string.IsNullOrWhiteSpace(exe)
            ? Array.Empty<RegistryTarget>()
            : new[] { GameValueTarget(GpuPreferences, exe) },
        ReadStatus = exe => ReadGpuPreferenceStatus(exe),
        Apply = exe => WriteGpuPreference(exe, true),
        Revert = exe => WriteGpuPreference(exe, false)
    };

    private static RegistryTarget GameValueTarget(RegistryTarget template, string exe)
        => new(template.Hive, template.Path, Path.GetFullPath(exe));

    private static TweakStatusInfo ReadGpuPreferenceStatus(string? exe)
    {
        if (string.IsNullOrWhiteSpace(exe))
            return new TweakStatusInfo { Status = TweakStatus.Unavailable, Detail = "игра не выбрана" };

        try
        {
            RegistryTarget target = GameValueTarget(GpuPreferences, exe);
            string? current = RegistryIo.ReadString(target);
            string? preference = TokenString.GetToken(current, "GpuPreference");
            bool applied = preference == "2";

            return new TweakStatusInfo
            {
                Status = applied ? TweakStatus.Applied : TweakStatus.NotApplied,
                Current = preference == null ? "предпочтение не задано" : "GpuPreference=" + preference,
                Target = "GpuPreference=2 (высокая производительность)",
                Detail = applied ? "для игры выбрана высокая производительность" : "Windows решает сама"
            };
        }
        catch (Exception ex)
        {
            return new TweakStatusInfo { Status = TweakStatus.Unknown, Detail = ex.Message };
        }
    }

    private static TweakOperationResult WriteGpuPreference(string? exe, bool highPerformance)
    {
        if (string.IsNullOrWhiteSpace(exe))
            return TweakOperationResult.Fail("сначала выберите игру");

        try
        {
            RegistryTarget target = GameValueTarget(GpuPreferences, exe);
            string? current = RegistryIo.ReadString(target);
            string updated = highPerformance
                ? TokenString.SetToken(current, "GpuPreference", "2")
                : TokenString.RemoveToken(current, "GpuPreference");

            if (string.IsNullOrWhiteSpace(updated))
            {
                RegistryIo.TryDeleteValue(target, out _);
            }
            else if (!RegistryIo.TryWrite(target, updated, RegistryValueKind.String, out string error))
            {
                return TweakOperationResult.Fail("UserGpuPreferences: " + error);
            }

            TweakStatusInfo status = ReadGpuPreferenceStatus(exe);
            bool ok = highPerformance ? status.Status == TweakStatus.Applied : status.Status != TweakStatus.Applied;
            return ok
                ? TweakOperationResult.Success(highPerformance
                    ? "для игры выбрана высокая производительность GPU (проверено)"
                    : "предпочтение GPU снято (проверено)")
                : TweakOperationResult.Fail("состояние не подтвердилось: " + status.Detail);
        }
        catch (Exception ex)
        {
            return TweakOperationResult.Fail("UserGpuPreferences: " + ex.Message);
        }
    }

    private static GameTweak FullscreenOptimizationsTweak() => new()
    {
        Id = IdFullscreenOptimizations,
        Title = "Отключить оптимизации полноэкранного режима",
        Group = TweakGroup.PerGame,
        Scope = TweakScope.PerGame,
        Effect = TweakEffectKind.InputLatency,
        EffectText = "игра получает прямой эксклюзивный полноэкранный режим",
        Mechanism = "Windows перестаёт подменять эксклюзивный полноэкранный режим на безрамочное окно (DISABLEDXMAXIMIZEDWINDOWEDMODE).",
        Limitation = "Может отключить оверлеи и Alt+Tab-поведение Windows. Существующие флаги совместимости сохраняются.",
        Risk = TweakRisk.Moderate,
        Recommended = true,
        SnapshotTargetsFor = exe => string.IsNullOrWhiteSpace(exe)
            ? Array.Empty<RegistryTarget>()
            : new[] { GameValueTarget(Layers, exe) },
        ReadStatus = exe => ReadFullscreenStatus(exe),
        Apply = exe => WriteFullscreen(exe, true),
        Revert = exe => WriteFullscreen(exe, false)
    };

    private const string FsoToken = "DISABLEDXMAXIMIZEDWINDOWEDMODE";

    private static TweakStatusInfo ReadFullscreenStatus(string? exe)
    {
        if (string.IsNullOrWhiteSpace(exe))
            return new TweakStatusInfo { Status = TweakStatus.Unavailable, Detail = "игра не выбрана" };

        try
        {
            RegistryTarget target = GameValueTarget(Layers, exe);
            string? current = RegistryIo.ReadString(target);
            bool applied = TokenString.HasLayerToken(current, FsoToken);

            return new TweakStatusInfo
            {
                Status = applied ? TweakStatus.Applied : TweakStatus.NotApplied,
                Current = string.IsNullOrWhiteSpace(current) ? "флаги совместимости не заданы" : current,
                Target = "~ " + FsoToken,
                Detail = applied ? "оптимизации полноэкранного режима отключены" : "действуют оптимизации Windows"
            };
        }
        catch (Exception ex)
        {
            return new TweakStatusInfo { Status = TweakStatus.Unknown, Detail = ex.Message };
        }
    }

    private static TweakOperationResult WriteFullscreen(string? exe, bool disabled)
    {
        if (string.IsNullOrWhiteSpace(exe))
            return TweakOperationResult.Fail("сначала выберите игру");

        try
        {
            RegistryTarget target = GameValueTarget(Layers, exe);
            string? current = RegistryIo.ReadString(target);
            string updated = disabled
                ? TokenString.AddLayerToken(current, FsoToken)
                : TokenString.RemoveLayerToken(current, FsoToken);

            if (string.IsNullOrWhiteSpace(updated))
            {
                RegistryIo.TryDeleteValue(target, out _);
            }
            else if (!RegistryIo.TryWrite(target, updated, RegistryValueKind.String, out string error))
            {
                return TweakOperationResult.Fail("AppCompatFlags\\Layers: " + error);
            }

            TweakStatusInfo status = ReadFullscreenStatus(exe);
            bool ok = disabled ? status.Status == TweakStatus.Applied : status.Status != TweakStatus.Applied;
            return ok
                ? TweakOperationResult.Success(disabled
                    ? "оптимизации полноэкранного режима отключены (проверено, остальные флаги сохранены)"
                    : "оптимизации полноэкранного режима возвращены (проверено, остальные флаги сохранены)")
                : TweakOperationResult.Fail("состояние не подтвердилось: " + status.Detail);
        }
        catch (Exception ex)
        {
            return TweakOperationResult.Fail("AppCompatFlags\\Layers: " + ex.Message);
        }
    }
}
