using System;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Win11CopyDialog.Modules.GameTuning;

/// <summary>
/// Р Р°Р±РѕС‚Р° СЃРѕ СЃС…РµРјР°РјРё РїРёС‚Р°РЅРёСЏ С‡РµСЂРµР· powercfg. РЎС…РµРјР° РјРµРЅСЏРµС‚СЃСЏ Р±РµР· РїСЂР°РІ
/// Р°РґРјРёРЅРёСЃС‚СЂР°С‚РѕСЂР° (СЌС‚Рѕ РЅР°СЃС‚СЂРѕР№РєР° С‚РµРєСѓС‰РµРіРѕ РїРѕР»СЊР·РѕРІР°С‚РµР»СЏ), РїРѕСЌС‚РѕРјСѓ
/// РїРµСЂРµРєР»СЋС‡РµРЅРёРµ Рё РїСЂРѕРІРµСЂРєР° РґРѕСЃС‚СѓРїРЅС‹ РѕР±С‹С‡РЅРѕРјСѓ РїСЂРѕС†РµСЃСЃСѓ.
/// </summary>
public static class PowerScheme
{
    public const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public const string UltimatePerformance = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    public const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    public const string PowerSaver = "a1841308-3541-4fab-bc81-f71556f20b4a";

    private static readonly Regex GuidRegex =
        new(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);

    public static (string Guid, string Name) GetActive()
    {
        (int code, string output) = Run("/getactivescheme");
        if (code != 0) return ("", "");

        Match match = GuidRegex.Match(output);
        if (!match.Success) return ("", output.Trim());

        string guid = match.Value;
        string name = guid;
        int open = output.IndexOf('(', match.Index);
        int close = output.LastIndexOf(')');
        if (open > 0 && close > open)
            name = output[(open + 1)..close].Trim();

        return (guid, name);
    }

    public static bool TryActivate(string guid, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(guid))
        {
            error = "РїСѓСЃС‚РѕР№ GUID СЃС…РµРјС‹ РїРёС‚Р°РЅРёСЏ";
            return false;
        }

        (int code, string output) = Run($"/setactive {guid}");
        if (code != 0)
        {
            error = string.IsNullOrWhiteSpace(output)
                ? $"powercfg Р·Р°РІРµСЂС€РёР»СЃСЏ СЃ РєРѕРґРѕРј {code}"
                : output.Trim();
            return false;
        }

        (string active, _) = GetActive();
        if (!string.Equals(active, guid, StringComparison.OrdinalIgnoreCase))
        {
            error = $"РїРѕСЃР»Рµ РєРѕРјР°РЅРґС‹ Р°РєС‚РёРІРЅР° РґСЂСѓРіР°СЏ СЃС…РµРјР°: {active}";
            return false;
        }

        return true;
    }

    public static bool IsHighPerformance(string guid)
        => string.Equals(guid, HighPerformance, StringComparison.OrdinalIgnoreCase)
           || string.Equals(guid, UltimatePerformance, StringComparison.OrdinalIgnoreCase);

    public static string FriendlyName(string guid) => guid.ToLowerInvariant() switch
    {
        HighPerformance => "Р’С‹СЃРѕРєР°СЏ РїСЂРѕРёР·РІРѕРґРёС‚РµР»СЊРЅРѕСЃС‚СЊ",
        UltimatePerformance => "РњР°РєСЃРёРјР°Р»СЊРЅР°СЏ РїСЂРѕРёР·РІРѕРґРёС‚РµР»СЊРЅРѕСЃС‚СЊ",
        Balanced => "РЎР±Р°Р»Р°РЅСЃРёСЂРѕРІР°РЅРЅР°СЏ",
        PowerSaver => "Р­РєРѕРЅРѕРјРёСЏ СЌРЅРµСЂРіРёРё",
        _ => guid
    };

    private static (int code, string output) Run(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("powercfg", arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using Process process = Process.Start(psi) ?? throw new InvalidOperationException("powercfg РЅРµ Р·Р°РїСѓСЃС‚РёР»СЃСЏ");
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(15000);
            return (process.HasExited ? process.ExitCode : -1, output);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
