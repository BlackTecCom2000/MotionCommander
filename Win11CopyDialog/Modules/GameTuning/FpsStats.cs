using System;
using System.Collections.Generic;
using System.Linq;

namespace Win11CopyDialog.Modules.GameTuning;

public sealed class FpsResult
{
    public string ProcessName { get; init; } = "";
    public int ProcessId { get; init; }
    public DateTime MeasuredAtUtc { get; init; } = DateTime.UtcNow;
    public double DurationSeconds { get; init; }
    public int FrameCount { get; init; }
    public double AverageFps { get; init; }
    public double MedianFrameMs { get; init; }
    public double P95FrameMs { get; init; }
    public double MaxFrameMs { get; init; }
    public double OnePercentLowFps { get; init; }
    public double PointOnePercentLowFps { get; init; }
    public int Stutters { get; init; }
    public string Source { get; init; } = "ETW DXGI/D3D9 Present";

    public bool IsMeasured => FrameCount >= 2;

    public string ShortSummary => IsMeasured
        ? $"{AverageFps:0.0} FPS, 1% low {OnePercentLowFps:0.0}, кадров {FrameCount}"
        : "нет данных";

    public string Line =>
        $"{(IsMeasured ? $"{AverageFps:0.0} FPS" : "н/д")} | 1% low {OnePercentLowFps:0.0} | 0.1% low {PointOnePercentLowFps:0.0} | " +
        $"медиана {MedianFrameMs:0.00} мс | p95 {P95FrameMs:0.00} мс | фризы {Stutters} | кадров {FrameCount}";
}

/// <summary>
/// Математика кадров отделена от ETW: её можно проверить на синтетических
/// временах кадров без игры, прав администратора и видеокарты.
/// </summary>
public static class FpsStats
{
    /// <summary>Порог, ниже которого изменение считается шумом (3%).</summary>
    public const double NoiseThresholdPercent = 3.0;

    public static FpsResult FromFrameTimes(
        IReadOnlyList<double> frameTimesMs,
        string processName,
        int processId,
        double durationSeconds = 0)
    {
        if (frameTimesMs == null || frameTimesMs.Count < 2)
        {
            return new FpsResult
            {
                ProcessName = processName,
                ProcessId = processId,
                FrameCount = frameTimesMs?.Count ?? 0,
                DurationSeconds = durationSeconds
            };
        }

        double[] times = frameTimesMs.Where(t => t > 0 && !double.IsNaN(t) && !double.IsInfinity(t))
                                     .OrderBy(t => t).ToArray();

        if (times.Length < 2)
        {
            return new FpsResult
            {
                ProcessName = processName,
                ProcessId = processId,
                FrameCount = times.Length,
                DurationSeconds = durationSeconds
            };
        }

        double mean = times.Average();
        double median = PercentileNearest(times, 50);
        double p95 = PercentileNearest(times, 95);
        double max = times[^1];

        int worst1 = Math.Max(1, (int)Math.Ceiling(times.Length * 0.01));
        int worst01 = Math.Max(1, (int)Math.Ceiling(times.Length * 0.001));

        double onePercentLow = 1000.0 / times.TakeLast(worst1).Average();
        double pointOneLow = 1000.0 / times.TakeLast(worst01).Average();

        double stutterLimit = Math.Max(2.0 * median, 33.34);
        int stutters = times.Count(t => t > stutterLimit);

        double duration = durationSeconds > 0 ? durationSeconds : times.Sum() / 1000.0;

        return new FpsResult
        {
            ProcessName = processName,
            ProcessId = processId,
            DurationSeconds = duration,
            FrameCount = times.Length,
            AverageFps = 1000.0 / mean,
            MedianFrameMs = median,
            P95FrameMs = p95,
            MaxFrameMs = max,
            OnePercentLowFps = onePercentLow,
            PointOnePercentLowFps = pointOneLow,
            Stutters = stutters
        };
    }

    public static double PercentileNearest(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 0) return 0;
        int index = (int)Math.Ceiling(percentile / 100.0 * sorted.Count) - 1;
        index = Math.Clamp(index, 0, sorted.Count - 1);
        return sorted[index];
    }

    /// <summary>
    /// Сравнение двух замеров. Если разница меньше 3%, результат честно
    /// называется шумом: заявлять прирост в пределах погрешности нельзя.
    /// </summary>
    public static FpsComparison Compare(FpsResult before, FpsResult after, double thresholdPercent = NoiseThresholdPercent)
    {
        if (!before.IsMeasured || !after.IsMeasured)
        {
            return new FpsComparison
            {
                Before = before,
                After = after,
                Verdict = "Недостаточно данных: нужно минимум по два кадра в каждом замере.",
                IsWithinNoise = true
            };
        }

        if (before.DurationSeconds < 3 || after.DurationSeconds < 3)
        {
            return new FpsComparison
            {
                Before = before,
                After = after,
                Verdict = "Замер короче 3 секунд — сравнивать нельзя.",
                IsWithinNoise = true
            };
        }

        double delta = before.AverageFps > 0
            ? (after.AverageFps - before.AverageFps) / before.AverageFps * 100.0
            : 0;

        double lowDelta = before.OnePercentLowFps > 0
            ? (after.OnePercentLowFps - before.OnePercentLowFps) / before.OnePercentLowFps * 100.0
            : 0;

        bool noise = Math.Abs(delta) < thresholdPercent;

        string verdict = noise
            ? $"Средний FPS изменился на {delta:+0.0;-0.0;0.0}% — это в пределах порога погрешности {thresholdPercent:0.#}%. Прирост не заявляется."
            : delta > 0
                ? $"Измеренный прирост среднего FPS: +{delta:0.0}% (порог погрешности {thresholdPercent:0.#}%). 1% low: {lowDelta:+0.0;-0.0;0.0}%."
                : $"Измеренное снижение среднего FPS: {delta:0.0}%. На этой конфигурации набор настроек не помог.";

        return new FpsComparison
        {
            Before = before,
            After = after,
            DeltaPercent = delta,
            OnePercentLowDeltaPercent = lowDelta,
            IsWithinNoise = noise,
            Verdict = verdict
        };
    }
}

public sealed class FpsComparison
{
    public FpsResult Before { get; init; } = new();
    public FpsResult After { get; init; } = new();
    public double DeltaPercent { get; init; }
    public double OnePercentLowDeltaPercent { get; init; }
    public bool IsWithinNoise { get; init; } = true;
    public string Verdict { get; init; } = "";
}

public sealed class FpsUnavailableException : Exception
{
    public FpsUnavailableException(string message) : base(message) { }
}
