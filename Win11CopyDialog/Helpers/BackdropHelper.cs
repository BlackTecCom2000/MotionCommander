using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Win11CopyDialog.Helpers;

/// <summary>Тип системного фона Windows 11 (DWM).</summary>
public enum BackdropType
{
    None = 1,
    Mica = 2,
    Acrylic = 3,
    MicaAlt = 4
}

public enum WindowCornerPreference
{
    Default = 0,
    DoNotRound = 1,
    Round = 2,
    RoundSmall = 3
}

/// <summary>
/// P/Invoke обёртка над DWM: Mica / Acrylic / скругление углов / тёмный режим.
/// На Windows 10 и ниже методы безопасно ничего не делают.
/// </summary>
public static class BackdropHelper
{
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    public static void Apply(Window window, BackdropType backdrop, bool darkMode, WindowCornerPreference corners = WindowCornerPreference.Round)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                // Окно ещё не создано — применить после SourceInitialized
                window.SourceInitialized += (_, _) => Apply(window, backdrop, darkMode, corners);
                return;
            }

            int corner = (int)corners;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

            int dark = darkMode ? 1 : 0;
            try { DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int)); } catch { /* Win10 1809+ ok */ }

            if (IsWindows11)
            {
                int type = (int)backdrop;
                DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int));
            }
        }
        catch
        {
            // Никогда не роняем приложение из-за декораций
        }
    }
}
