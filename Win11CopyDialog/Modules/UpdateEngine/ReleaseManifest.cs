using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Win11CopyDialog.Modules.UpdateEngine;

/// <summary>
/// Отказ по целостности обновления.
/// </summary>
/// <remarks>
/// Отдельный тип нужен, чтобы вызывающий код отличал подмену или
/// повреждение файла от обычной сетевой ошибки и сообщал пользователю
/// разные вещи: в одном случае достаточно повторить загрузку, в
/// другом требуется разбираться с источником.
/// </remarks>
public sealed class UpdateIntegrityException : Exception
{
    public UpdateIntegrityException(string message) : base(message) { }
    public UpdateIntegrityException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Манифест релизов: ожидаемые контрольные суммы файлов обновления.
/// </summary>
/// <remarks>
/// <para>Хранится рядом с файлом <c>version.json</c> на том же
/// сервере и имеет то же имя версии. Приложение читает его перед
/// применением обновления и сверяет SHA-256 скачанного файла.</para>
///
/// <para>Сам манифест не подписан: см. замечание в
/// <see cref="UpdateService"/>. Это закрывает подмену файла обновления,
/// но не подмену манифеста.</para>
/// </remarks>
internal static class ReleaseManifest
{
    private const string ManifestName = "checksums.json";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>
    /// Возвращает ожидаемый SHA-256 файла по его имени.
    /// </summary>
    /// <remarks>
    /// <para>Сравнение идёт по имени файла, а не по полному адресу:
    /// манифест публикуется один на релиз и не должен содержать
    /// повторяющиеся полные адреса, которые расходятся при смене
    /// способа раздачи.</para>
    ///
    /// <para>Пустая строка означает «записи нет» и приводит к отказу
    /// обновления, а не к применению без проверки. Так поступать
    /// безопаснее: отсутствие подтверждения не должно читаться как
    /// разрешение.</para>
    /// </remarks>
    public static async Task<string> GetSha256ForAsync(string filePath, CancellationToken ct)
    {
        string name = Path.GetFileName(filePath);
        if (string.IsNullOrEmpty(name)) return string.Empty;

        string json = await FetchManifestAsync(ct);
        if (string.IsNullOrWhiteSpace(json)) return string.Empty;

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("files", out var files)) return string.Empty;
        if (!files.TryGetProperty(name, out var hash)) return string.Empty;

        string value = hash.GetString() ?? string.Empty;

        // Хэш обязан быть ровно 64 шестнадцатеричные цифры: любая
        // другая длина означает повреждение манифеста, и сравнение
        // с ней дало бы ложное расхождение или, что хуже, принятие
        // короткой строки за совпадение.
        if (value.Length != 64) return string.Empty;

        foreach (char c in value)
            if (!Uri.IsHexDigit(c)) return string.Empty;

        return value.ToUpperInvariant();
    }

    private static async Task<string> FetchManifestAsync(CancellationToken ct)
    {
        foreach (string baseUrl in ManifestBaseUrls())
        {
            try
            {
                using var response = await Client.GetAsync(baseUrl + ManifestName, ct);
                if (!response.IsSuccessStatusCode) continue;

                return await response.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Сеть недоступна на этом адресе: пробуем следующий.
                // Молча продолжать нельзя — отсутствие манифеста
                // приведёт к отказу обновления, и пользователь должен
                // узнать об этом из сообщения, а не из пустого лога.
            }
        }

        return string.Empty;
    }

    /// <summary>Адреса, на которых ищется манифест, по порядку.</summary>
    private static IEnumerable<string> ManifestBaseUrls()
    {
        yield return "https://raw.githubusercontent.com/BlackTecCom2000/MotionCommander/main/dist/";
    }
}
