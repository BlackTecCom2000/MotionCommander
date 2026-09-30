using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Win11CopyDialog.Helpers;

/// <summary>
/// Потоковая проверка целостности файла по CRC-32.
///
/// <para><b>Зачем это нужно.</b> В настройках была галочка
/// «Автоматический расчёт CRC-32 контрольной суммы на лету», которая
/// сохранялась в settings.json и не читалась НИ ОДНИМ движком. Пользователю
/// обещалась проверка целостности при копировании, которой не существовало.</para>
///
/// <para>Реализация считает CRC «на лету» во втором проходе по файлу, не
/// создавая временных копий. Контрольная сумма считается блоками с использованием
/// ArrayPool, поэтому проверка больших файлов не засоряет LOH и не съедает память.</para>
/// </summary>
public static class Crc32Verifier
{
    private const int BlockSize = 1 << 20;   // 1 МБ

    /// <summary>Таблица CRC-32 (стандартный полином 0xEDB88320).</summary>
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    /// <summary>Вычисляет CRC-32 файла.</summary>
    /// <returns>Контрольная сумма, либо null при ошибке чтения/отмене.</returns>
    public static async Task<uint?> ComputeFileCrc32Async(string path, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(path)) return null;

            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                BlockSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

            byte[] buffer = ArrayPool<byte>.Shared.Rent(BlockSize);
            try
            {
                uint crc = 0xFFFFFFFFu;

                int read;
                while ((read = await stream.ReadAsync(buffer.AsMemory(0, BlockSize), ct).ConfigureAwait(false)) > 0)
                {
                    crc = Update(crc, buffer, read);
                }

                return ~crc;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>CRC-32 одного буфера — для синхронных мест.</summary>
    public static uint ComputeBytes(byte[] data) => ~Update(0xFFFFFFFFu, data, data.Length);

    private static uint Update(uint crc, byte[] buffer, int count)
    {
        for (int i = 0; i < count; i++)
            crc = Table[(crc ^ buffer[i]) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    /// <summary>
    /// Сравнивает CRC-32 источника и копии.
    ///
    /// <para>Результат однозначен: true только если ОБА файла удалось
    /// прочитать и суммы совпали. Если вычисление не удалось, метод
    /// возвращает false вместе с причиной — молчаливый «успех» здесь был бы
    /// тем же обманом, что и отсутствующая проверка.</para>
    /// </summary>
    public static async Task<(bool match, string detail)> VerifyMatchAsync(
        string source, string destination, CancellationToken ct = default)
    {
        uint? src = await ComputeFileCrc32Async(source, ct).ConfigureAwait(false);
        if (src is null)
            return (false, "не удалось прочитать исходный файл для сверки");

        uint? dst = await ComputeFileCrc32Async(destination, ct).ConfigureAwait(false);
        if (dst is null)
            return (false, "не удалось прочитать скопированный файл для сверки");

        if (src.Value != dst.Value)
            return (false, $"контрольные суммы не совпали: источник {src.Value:X8}, копия {dst.Value:X8}");

        return (true, $"CRC-32 {src.Value:X8} совпал");
    }
}
