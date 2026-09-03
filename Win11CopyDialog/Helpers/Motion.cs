namespace Win11CopyDialog.Helpers;

/// <summary>
/// Единая motion-система: длительности, easing, интерполяция.
/// Длительности по брифу: micro 100–180мс, normal 180–300мс, large 300–600мс, cinematic 600–1200мс.
/// </summary>
public static class Motion
{
    public const double Micro = 150;
    public const double Normal = 240;
    public const double Large = 450;
    public const double Cinematic = 900;

    public static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    /// <summary>Экспоненциальное сглаживание, независимое от FPS. rate ~ скорость (1/с).</summary>
    public static double Damp(double current, double target, double rate, double dt)
    {
        if (dt <= 0) return current;
        double t = 1 - Math.Exp(-rate * dt);
        return current + (target - current) * t;
    }

    public static double Lerp(double a, double b, double t) => a + (b - a) * t;

    public static double EaseOutCubic(double t)
    {
        t = Clamp01(t);
        return 1 - Math.Pow(1 - t, 3);
    }

    public static double EaseInOutCubic(double t)
    {
        t = Clamp01(t);
        return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    }

    /// <summary>Пружина для press/release микроанимаций. Возвращает новое значение и скорость.</summary>
    public static (double value, double velocity) Spring(double value, double velocity, double target,
        double stiffness, double damping, double dt)
    {
        double f = -stiffness * (value - target) - damping * velocity;
        velocity += f * dt;
        value += velocity * dt;
        return (value, velocity);
    }
}
