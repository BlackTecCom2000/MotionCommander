using System.IO;
using System.Windows;

namespace Win11CopyDialog.Helpers;

/// <summary>
/// Реквизиты для доната автора.
///
/// ВАЖНО (PCI-DSS): полные номера банковских карт (PAN) запрещено хранить
/// в исходном коде, в сборке и в публичных репозиториях. Допустимо хранить
/// только маскированный вид (последние 4 цифры).
///
/// Полный номер берётся из переменной окружения пользователя
/// (MOTIONCOMMANDER_DONATE_CARD) либо из локального файла, который НЕ входит
/// в систему контроля версий. Если значение недоступно, вместо полного номера
/// показывается маска, а кнопка копирования сообщает об этом пользователю.
/// </summary>
public sealed record DonationMethod(
    string Bank,
    string CardMask,
    string EnvVariable,
    string FileName)
{
    public bool IsFullNumberAvailable => DonationInfo.TryReadFullNumber(EnvVariable, FileName, out _);

    /// <summary>Возвращает полный номер только если он реально доступен во внешнем источнике.</summary>
    public bool TryGetFullNumber(out string pan)
    {
        if (DonationInfo.TryReadFullNumber(EnvVariable, FileName, out var value))
        {
            pan = DonationInfo.FormatPan(value);
            return true;
        }

        pan = "";
        return false;
    }
}

public static class DonationInfo
{
    private const string ConfigDirName = "MotionCommander";

    public static readonly DonationMethod Alif = new(
        Bank: "Alif Bank VISA",
        CardMask: "•••• •••• •••• 6013",
        EnvVariable: "MOTIONCOMMANDER_DONATE_CARD_ALIF",
        FileName: "donate_alif.txt");

    public static readonly DonationMethod DcBank = new(
        Bank: "DC Bank VISA",
        CardMask: "•••• •••• •••• 1431",
        EnvVariable: "MOTIONCOMMANDER_DONATE_CARD_DC",
        FileName: "donate_dc.txt");

    public static IReadOnlyList<DonationMethod> All { get; } = new[] { Alif, DcBank };

    /// <summary>
    /// Ищет полный номер в переменной окружения, затем в локальном файле.
    /// Возвращает false, если значение недоступно (тогда показывается маска).
    /// </summary>
    internal static bool TryReadFullNumber(string envVariable, string fileName, out string pan)
    {
        pan = "";

        var fromEnv = Environment.GetEnvironmentVariable(envVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv) && IsValidPan(fromEnv))
        {
            pan = fromEnv.Trim();
            return true;
        }

        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ConfigDirName);
            var path = Path.Combine(dir, fileName);

            if (File.Exists(path))
            {
                var fromFile = File.ReadAllText(path).Trim();
                if (IsValidPan(fromFile))
                {
                    pan = fromFile;
                    return true;
                }
            }
        }
        catch
        {
            // Недоступная файловая система или нет прав — просто показываем маску.
        }

        return false;
    }

    private static bool IsValidPan(string value)
    {
        var digits = value.Replace(" ", "").Replace("-", "");
        return digits.Length is >= 12 and <= 19 && digits.All(char.IsAsciiDigit);
    }

    /// <summary>Форматирует сырой номер в вид "0000 0000 0000 0000" (для примера).</summary>
    internal static string FormatPan(string value)
    {
        var digits = value.Replace(" ", "").Replace("-", "");
        if (digits.Length <= 4) return digits;

        var groups = new List<string>();
        for (int i = 0; i < digits.Length; i += 4)
        {
            groups.Add(digits.Substring(i, Math.Min(4, digits.Length - i)));
        }

        return string.Join(" ", groups);
    }

    /// <summary>
    /// Копирует полный номер в буфер обмена, если он доступен.
    /// Возвращает текст статуса для показа пользователю.
    /// </summary>
    public static string CopyToClipboard(DonationMethod method, out bool success)    {
        if (!method.TryGetFullNumber(out var pan))
        {
            success = false;
            return $"Полный номер карты {method.Bank} недоступен на этой машине.\n\n" +
                   $"Показана маска: {method.CardMask}\n\n" +
                   $"Чтобы включить копирование, создайте файл \"%LOCALAPPDATA%\\MotionCommander\\{method.FileName}\" " +
                   $"с содержимым номера карты, либо задайте переменную окружения {method.EnvVariable}.";
        }

        try
        {
            Clipboard.SetText(pan);
            success = true;
            return $"Номер карты {method.Bank} скопирован в буфер обмена. Спасибо за поддержку разработки!";
        }
        catch (Exception)
        {
            // Clipboard может быть занят другим процессом — это не критично.
            success = false;
            return "Не удалось скопировать: буфер обмена сейчас используется другим приложением. Попробуйте ещё раз.";
        }
    }

    /// <summary>Разбирает Tag кнопки ("alif" / "dc") в объект метода.</summary>
    public static bool TryParseTag(string tag, out DonationMethod method)
    {
        switch (tag.Trim().ToLowerInvariant())
        {
            case "alif":
                method = Alif;
                return true;
            case "dc":
            case "dcbank":
                method = DcBank;
                return true;
            default:
                method = Alif;
                return false;
        }
    }
}
