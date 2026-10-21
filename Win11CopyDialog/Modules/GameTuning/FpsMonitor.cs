using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using Win11CopyDialog.Helpers;

namespace Win11CopyDialog.Modules.GameTuning;

public sealed record FpsProgress(double Fraction, int Frames, double ElapsedSeconds);

/// <summary>
/// Измерение частоты кадров по событиям Present из ETW-провайдеров
/// Microsoft-Windows-DXGI и Microsoft-Windows-D3D9.
///
/// Это единственный способ получить настоящий FPS без внедрения в игру.
/// Создание ETW-сессии Windows разрешает только администратору — проверено
/// на этой машине: без прав возвращается «Access Denied». Поэтому замер
/// не «делает вид», а честно требует права и объясняет отказ.
///
/// Ограничения, о которых интерфейс обязан сказать:
/// <list type="bullet">
/// <item>игры на Vulkan/OpenGL не выводят кадры через DXGI и не измеряются;</item>
/// <item>свёрнутая игра не выводит кадры — замер вернёт «кадры не получены»;</item>
/// <item>события Present не содержат GPU-времени, поэтому 1% low считается
/// по интервалам между кадрами, а не по времени рендера.</item>
/// </list>
/// </summary>
public static class FpsMonitor
{
    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9Provider = new("783ACA0A-790E-4D7F-8451-A4C33EA1B2A3");

    public static bool CanMeasure => SuperAdminPrivilegeHelper.IsAdministrator();

    public static string CapabilityText => CanMeasure
        ? "ETW доступен: процесс запущен с правами администратора"
        : "ETW недоступен: нужны права администратора (программа запущена без повышения)";

    public static async Task<FpsResult> MeasureAsync(
        int processId,
        TimeSpan duration,
        IProgress<FpsProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!CanMeasure)
        {
            throw new FpsUnavailableException(
                "Замер FPS невозможен: Windows не разрешает создать ETW-сессию без прав администратора " +
                "(проверено: «Access Denied (Administrator rights required to start ETW)»). " +
                "Запустите Motion Commander с повышением прав и повторите замер.");
        }

        if (processId <= 0)
            throw new FpsUnavailableException("Не указан процесс игры: выберите запущенную игру.");

        if (duration < TimeSpan.FromSeconds(3))
            duration = TimeSpan.FromSeconds(3);

        return await Task.Run(() => MeasureCore(processId, duration, progress, cancellationToken), cancellationToken);
    }

    private static FpsResult MeasureCore(
        int processId,
        TimeSpan duration,
        IProgress<FpsProgress>? progress,
        CancellationToken cancellationToken)
    {
        string processName = "";
        try
        {
            using Process process = Process.GetProcessById(processId);
            processName = process.ProcessName;
        }
        catch (Exception ex)
        {
            throw new FpsUnavailableException($"Процесс {processId} недоступен: {ex.Message}");
        }

        string sessionName = "MotionCommander-FPS-" + Guid.NewGuid().ToString("N")[..8];
        var timestamps = new List<double>(16384);
        object gate = new();
        string pumpError = "";

        using var session = new TraceEventSession(sessionName) { StopOnDispose = true };

        try
        {
            session.EnableProvider(DxgiProvider, TraceEventLevel.Verbose, ulong.MaxValue);
            session.EnableProvider(D3D9Provider, TraceEventLevel.Verbose, ulong.MaxValue);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new FpsUnavailableException(
                "Windows отказала в создании ETW-сессии: " + ex.Message +
                ". Нужны права администратора.");
        }
        catch (Exception ex)
        {
            throw new FpsUnavailableException("Не удалось включить провайдеры кадров: " + ex.Message);
        }

        var pump = new Thread(() =>
        {
            try
            {
                session.Source.AllEvents += data =>
                {
                    if (data.ProcessID != processId) return;
                    if (!IsPresentEvent(data)) return;
                    lock (gate) timestamps.Add(data.TimeStampRelativeMSec);
                };
                session.Source.Process();
            }
            catch (Exception ex)
            {
                pumpError = ex.Message;
            }
        })
        {
            IsBackground = true,
            Name = "FPS ETW pump"
        };

        pump.Start();

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < duration)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                session.Dispose();
                break;
            }

            int frames;
            lock (gate) frames = timestamps.Count;
            progress?.Report(new FpsProgress(
                Math.Clamp(stopwatch.Elapsed.TotalSeconds / duration.TotalSeconds, 0, 1),
                frames,
                stopwatch.Elapsed.TotalSeconds));

            Thread.Sleep(200);
        }

        session.Dispose();
        if (!pump.Join(TimeSpan.FromSeconds(5)))
            pumpError = string.IsNullOrEmpty(pumpError) ? "ETW-поток не завершился" : pumpError;

        double[] times;
        lock (gate) times = timestamps.OrderBy(t => t).ToArray();

        if (times.Length < 2)
        {
            string reason = string.IsNullOrEmpty(pumpError) ? "" : " Ошибка ETW: " + pumpError;
            throw new FpsUnavailableException(
                $"За {duration.TotalSeconds:0} с не получено ни одного кадра процесса {processName} (PID {processId})." +
                " Проверьте: игра запущена и не свёрнута, использует DirectX 10–12 (или D3D9), и выбран именно её процесс." +
                reason);
        }

        var frameTimes = new List<double>(times.Length - 1);
        for (int i = 1; i < times.Length; i++)
        {
            double delta = times[i] - times[i - 1];
            if (delta > 0) frameTimes.Add(delta);
        }

        double measuredSeconds = (times[^1] - times[0]) / 1000.0;
        FpsResult result = FpsStats.FromFrameTimes(frameTimes, processName, processId, measuredSeconds);

        if (!result.IsMeasured)
            throw new FpsUnavailableException("Получено слишком мало кадров для расчёта: " + times.Length);

        return result;
    }

    private static bool IsPresentEvent(TraceEvent data)
    {
        if (data.ProviderGuid == DxgiProvider)
            return (int)data.ID is 42 or 43; // DXGI Present / PresentMultiplaneOverlay

        if (data.ProviderGuid == D3D9Provider)
            return (int)data.ID == 1; // D3D9 Present

        return false;
    }

    public static int? FindProcessId(string nameOrPid)
    {
        if (string.IsNullOrWhiteSpace(nameOrPid)) return null;

        if (int.TryParse(nameOrPid, out int pid))
        {
            try
            {
                using Process process = Process.GetProcessById(pid);
                return process.Id;
            }
            catch
            {
                return null;
            }
        }

        string name = nameOrPid.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];

        var matches = Process.GetProcessesByName(name);
        try
        {
            return matches.Length == 0 ? null : matches[0].Id;
        }
        finally
        {
            foreach (Process process in matches) process.Dispose();
        }
    }
}
