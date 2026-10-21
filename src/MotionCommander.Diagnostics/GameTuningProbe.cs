#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Win11CopyDialog.Modules.GameTuning;

namespace MotionCommander.Diagnostics;

/// <summary>
/// Чтение состояния игровых настроек и замер FPS из командной строки.
/// Отчёт только читает систему: ни один твик здесь не применяется, чтобы
/// диагностика не меняла настройки пользователя.
/// </summary>
public static class GameTuningProbe
{
    public static int Run(TextWriter output, string? gameExe)
    {
        HardwareInfo hardware = HardwareProbe.Probe();
        GameTuningService service = GameTuningService.Instance;

        output.WriteLine(service.BuildStatusReport(hardware, gameExe));
        output.WriteLine();

        output.WriteLine("КОНФИГИ ИГР (только предпросмотр, запись не выполняется):");
        foreach (ConfigPresetInfo preset in GameConfigPresets.Presets)
        {
            ConfigPreview preview = GameConfigPresets.BuildPreview(preset.Id, gameExe, hardware.RefreshHz);
            output.WriteLine($"  {preset.Title}");
            output.WriteLine($"    файл: {(string.IsNullOrEmpty(preview.FilePath) ? "не найден" : preview.FilePath)}");
            output.WriteLine($"    существует: {(preview.FileExists ? "да" : "нет")}; игра запущена: {(preview.GameRunning ? "да" : "нет")}");
            output.WriteLine("    " + preview.Summary);
            foreach (ConfigChange change in preview.Changes)
                output.WriteLine("      " + change.Line);
            output.WriteLine();
        }

        output.WriteLine(new string('=', 60));
        output.WriteLine("ЧЕСТНО О ГРАНИЦАХ:");
        output.WriteLine("  • Твики, требующие HKLM, помечены и без прав администратора не применяются.");
        output.WriteLine("  • Замер FPS использует ETW (DXGI/D3D9 Present) и требует прав администратора.");
        output.WriteLine("    Без них команда --fps завершается отказом, а не выдуманными числами.");
        output.WriteLine("  • Игры на Vulkan/OpenGL через DXGI не измеряются.");
        output.WriteLine("  • Конфиги игр меняются только по существующим ключам и с резервной копией.");
        output.WriteLine(new string('=', 60));

        return 0;
    }

    public static int Fps(TextWriter output, string processNameOrPid, int seconds)
    {
        if (seconds < 3) seconds = 3;
        if (seconds > 120) seconds = 120;

        int? pid = FpsMonitor.FindProcessId(processNameOrPid);
        if (!pid.HasValue)
        {
            output.WriteLine($"НЕ ИЗМЕРЕНО: процесс «{processNameOrPid}» не найден. Запустите игру и повторите.");
            return 2;
        }

        output.WriteLine($"Процесс: {processNameOrPid} (PID {pid.Value}), длительность {seconds} с");
        output.WriteLine("Источник: ETW Microsoft-Windows-DXGI / Microsoft-Windows-D3D9 (Present)");

        if (!FpsMonitor.CanMeasure)
        {
            output.WriteLine();
            output.WriteLine("НЕ ИЗМЕРЕНО: Windows не разрешает ETW-сессию без прав администратора.");
            output.WriteLine("Проверено на этой машине: «Access Denied (Administrator rights required to start ETW)».");
            output.WriteLine("Запустите диагностику или программу от имени администратора и повторите команду.");
            return 3;
        }

        try
        {
            double nextReportAt = 0;
            var progress = new Progress<FpsProgress>(p =>
            {
                if (p.ElapsedSeconds >= nextReportAt)
                {
                    output.WriteLine($"  ... {p.ElapsedSeconds:0.0} с, кадров {p.Frames}");
                    nextReportAt = p.ElapsedSeconds + 2.0;
                }
            });

            FpsResult result = FpsMonitor.MeasureAsync(pid.Value, TimeSpan.FromSeconds(seconds), progress)
                                          .GetAwaiter().GetResult();

            output.WriteLine();
            output.WriteLine("РЕЗУЛЬТАТ (измерено):");
            output.WriteLine("  " + result.Line);
            output.WriteLine($"  Длительность: {result.DurationSeconds:0.00} с, источник: {result.Source}");
            output.WriteLine($"  Процент кадров медленнее 2× медианы: {(result.FrameCount > 0 ? 100.0 * result.Stutters / result.FrameCount : 0):0.00}%");
            return 0;
        }
        catch (FpsUnavailableException ex)
        {
            output.WriteLine();
            output.WriteLine("НЕ ИЗМЕРЕНО: " + ex.Message);
            return 3;
        }
        catch (Exception ex)
        {
            output.WriteLine();
            output.WriteLine("НЕ ИЗМЕРЕНО: " + ex.GetType().Name + ": " + ex.Message);
            return 4;
        }
    }
}
