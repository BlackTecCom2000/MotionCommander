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
public static class ReleaseManifest
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
    public static async Task<string> GetSha256ForAsync(string filePath, CancellationToken ct, string? expectedVersion = null)
    {
        string name = Path.GetFileName(filePath);
        if (string.IsNullOrEmpty(name)) return string.Empty;

        // Если передан временный файл вида [Архив].[guid].part или [Архив].part
        if (name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
        {
            string withoutPart = Path.GetFileNameWithoutExtension(name);
            int lastDot = withoutPart.LastIndexOf('.');
            if (lastDot > 0)
            {
                name = withoutPart[..lastDot];
            }
            else
            {
                name = withoutPart;
            }
        }

        string json = await FetchManifestAsync(ct);
        if (string.IsNullOrWhiteSpace(json)) return string.Empty;

        using var doc = JsonDocument.Parse(json);

        // Манифест обязан относиться к той же версии, что и файл. При
        // устаревшем манифесте с CDN сверка давала бы «контрольная сумма
        // не совпала» — сообщение о подмене там, где на самом деле
        // просто закэшировался прошлый релиз.
        if (!string.IsNullOrWhiteSpace(expectedVersion) &&
            doc.RootElement.TryGetProperty("version", out var versionProp))
        {
            string manifestVersion = versionProp.GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(manifestVersion) &&
                !string.Equals(manifestVersion.TrimStart('v', 'V'),
                               expectedVersion.TrimStart('v', 'V'),
                               StringComparison.OrdinalIgnoreCase))
            {
                UpdateLog.Warn("integrity",
                    $"checksums.json относится к версии {manifestVersion}, а файл — к {expectedVersion}");
                throw new UpdateIntegrityException(
                    $"Манифест контрольных сумм относится к версии v{manifestVersion}, " +
                    $"а скачивается файл версии v{expectedVersion}. " +
                    "Вероятно, CDN отдал устаревший манифест; повторите обновление через минуту.");
            }
        }

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
        // 1. Локальная проверка (для оффлайн-режима, тестирования и портативных сборок с манифестом рядом)
        try
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string localChecksums = Path.Combine(baseDir, ManifestName);
            if (!File.Exists(localChecksums))
            {
                localChecksums = Path.Combine(baseDir, "dist", ManifestName);
            }
            if (File.Exists(localChecksums))
            {
                return await File.ReadAllTextAsync(localChecksums, ct);
            }
        }
        catch { }

        // 2. Сетевой запрос с cache-bypass для предотвращения устаревания CDN-кэша после релиза
        foreach (string baseUrl in ManifestBaseUrls())
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                string url = $"{baseUrl}{ManifestName}?cache_bypass={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
                using var response = await Client.GetAsync(url, ct);
                if (!response.IsSuccessStatusCode)
                {
                    // 404 на зеркале — обычное дело для только что созданного
                    // файла (CDN ещё не подхватил). Это не ошибка приложения,
                    // но в журнале должно быть видно, почему взят другой адрес.
                    UpdateLog.Warn("integrity",
                        $"{new Uri(baseUrl).Host}: HTTP {(int)response.StatusCode} для {ManifestName}");
                    continue;
                }

                string json = await response.Content.ReadAsStringAsync(ct);
                UpdateLog.Info("integrity",
                    $"{ManifestName} получен с {new Uri(baseUrl).Host} " +
                    $"({json.Length} байт за {sw.Elapsed.TotalMilliseconds:F0} мс)");
                return json;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Сеть недоступна на этом адресе: пробуем следующий.
                // Молча продолжать нельзя — отсутствие манифеста
                // приведёт к отказу обновления, и пользователь должен
                // узнать об этом из сообщения, а не из пустого лога.
                UpdateLog.Warn("integrity",
                    $"{new Uri(baseUrl).Host}: сбой за {sw.Elapsed.TotalMilliseconds:F0} мс — {ex.Message}");
            }
        }

        return string.Empty;
    }

    /// <summary>Адреса, на которых ищется манифест, по порядку.</summary>
    private static IEnumerable<string> ManifestBaseUrls()
    {
        // Замерено 20.10.2026: оба адреса отдают checksums.json;
        // jsDelivr — зеркало на случай недоступности raw.githubusercontent.
        // @latest, а не @main: ссылка на ветку кэшируется и после релиза
        // отдавала прошлый манифест.
        yield return "https://raw.githubusercontent.com/BlackTecCom2000/MotionCommander/main/dist/";
        yield return "https://cdn.jsdelivr.net/gh/BlackTecCom2000/MotionCommander@latest/dist/";
        yield return "https://github.com/BlackTecCom2000/MotionCommander/raw/main/dist/";
    }
}
