using System.Runtime.InteropServices;
using System.Windows.Media;
using Microsoft.Win32;

namespace Win11CopyDialog.Helpers;

/// <summary>Чтение системного акцента Windows 11 + форматирование единиц.</summary>
public static class SystemAccent
{
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetColorizationColor", PreserveSig = true)]
    private static extern int DwmGetColorizationColor(out uint pcrColorization, out bool pfOpaqueBlend);

    /// <summary>Системный акцентный цвет (DWM → реестр → fallback).</summary>
    public static Color GetSystemAccent()
    {
        try
        {
            if (DwmGetColorizationColor(out uint c, out _) == 0)
            {
                // 0xAARRGGBB → WPF Color
                return Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c);
            }
        }
        catch { /* ignore */ }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int dwm)
            {
                // ABGR → ARGB
                byte a = (byte)(dwm >> 24), b = (byte)(dwm >> 16), g = (byte)(dwm >> 8), r = (byte)dwm;
                return Color.FromArgb(a, r, g, b);
            }
        }
        catch { /* ignore */ }

        return (Color)ColorConverter.ConvertFromString("#0078D4");
    }

    /// <summary>Определяет, включена ли в Windows системная тёмная тема для приложений.</summary>
    public static bool IsSystemDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int val)
            {
                return val == 0;
            }
        }
        catch { /* ignore */ }
        return false;
    }
}

public static class Formatters
{
    private static readonly string[] Units = { "Б", "КБ", "МБ", "ГБ", "ТБ" };

    public static string Bytes(long bytes)
    {
        if (bytes < 0) bytes = 0;
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < Units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} {Units[u]}" : $"{v:0.#} {Units[u]}";
    }

    public static string Speed(double bytesPerSec)
    {
        if (bytesPerSec < 0.5) return "0 КБ/с";
        return Bytes((long)bytesPerSec) + "/с";
    }

    public static string Eta(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} ч {t.Minutes} мин";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes} мин {t.Seconds} с";
        return $"~{Math.Max(1, (int)Math.Ceiling(t.TotalSeconds))} с";
    }

    public static string Elapsed(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
}
