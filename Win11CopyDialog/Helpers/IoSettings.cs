using System;
using System.IO;
using Win11CopyDialog.Views.Dialogs;

namespace Win11CopyDialog.Helpers;

/// <summary>
/// Параметры ввода-вывода в единственном экземпляре, читаемые движками.
///
/// <para><b>Зачем этот класс.</b> Пять параметров панели «Параметры I/O»
/// (размер буфера, число потоков, Direct I/O, последовательное чтение,
/// авто-CRC-32) были объявлены в AppConfigData, записывались в settings.json
/// и НЕ ЧИТАЛИСЬ НИ ОДНИМ ДВИЖКОМ. При этом комментарий в коде утверждал
/// обратное: «параметры, реально читаются движками», а чекбокс
/// «Автоматический расчёт CRC-32 контрольной суммы на лету» обещал
/// пользователю проверку целостности, которой не существовало.</para>
///
/// <para>Теперь это единственное место, откуда движки берут свои настройки,
/// а значения приходят из живого AppConfigData, поэтому изменение флажка в
/// настройках действует без перезапуска.</para>
/// </summary>
public static class IoSettings
{
    /// <summary>Размер буфера копирования, байт. По умолчанию 4 МБ.</summary>
    public static int BufferSizeBytes
    {
        get
        {
            int kb = Config.DefaultBufferSizeKb;
            // Границы защищены: пользовательский ввод не должен приводить
            // к выделению 1 байта или к выделению гигабайта на файл.
            return Math.Clamp(kb, 64, 8192) * 1024;
        }
    }

    /// <summary>Число параллельных потоков копирования.</summary>
    public static int Concurrency => Math.Clamp(Config.ConcurrencyThreads, 1, 16);

    /// <summary>
    /// Обход кэша при записи (FILE_FLAG_NO_BUFFERING).
    ///
    /// <para>Требует выравнивания по сектору, иначе Windows возвращает
    /// ошибку. Поэтому применяется только когда пользователь явно выбрал
    /// этот режим И размер буфера кратен сектору.</para>
    /// </summary>
    public static bool DirectIo => Config.DirectIoBypassCache;

    /// <summary>Подсказка последовательного доступа при чтении.</summary>
    public static bool SequentialScan => Config.SequentialScanOptimized;

    /// <summary>
    /// Проверять целостность копии по CRC-32 после каждого файла.
    /// По умолчанию включено — как и обещает флажок в настройках.
    /// </summary>
    public static bool VerifyCrc32 => Config.AutoVerifyCrc32;

    /// <summary>Текущие значения из конфигурации.</summary>
    private static AppConfigData Config => AppConfigData.Instance;

    /// <summary>
    /// Размер логического сектора системного диска, байты.
    ///
    /// <para>Читается один раз из WMI (MSFT_PhysicalDisk.LogicalSectorSize) и
    /// кэшируется. DriveInfo не подходит: у него нет ClusterSize, а
    /// DriveInfo не реализует IDisposable, поэтому конструкция using с ним
    /// не компилируется.</para>
    ///
    /// <para>Нужен для FILE_FLAG_NO_BUFFERING: обход кэша требует, чтобы
    /// размер буфера был кратен размеру сектора, иначе Windows возвращает
    /// ошибку. Значение по умолчанию 4096 — почти у всех современных дисков.</para>
    /// </summary>
    private static readonly Lazy<int> _sectorSize = new(ReadSectorSize);

    private static int ReadSectorSize()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT LogicalSectorSize FROM Win32_DiskDrive");
            foreach (var obj in searcher.Get())
            {
                int size = Convert.ToInt32(obj["LogicalSectorSize"] ?? 0);
                if (size >= 512 && size <= 65536 && (size & (size - 1)) == 0)
                    return size;
            }
        }
        catch { }
        return 4096;
    }

    /// <summary>Размер сектора, байты (по умолчанию 4096).</summary>
    public static int SectorSize => _sectorSize.Value;

    /// <summary>
    /// Флаги открытия файла для чтения согласно настройкам.
    /// </summary>
    public static FileOptions ReadOptions()
    {
        FileOptions opts = FileOptions.Asynchronous;
        if (SequentialScan) opts |= FileOptions.SequentialScan;
        return opts;
    }

    /// <summary>
    /// Флаги открытия файла для записи согласно настройкам.
    /// </summary>
    public static FileOptions WriteOptions()
    {
        FileOptions opts = FileOptions.Asynchronous;
        if (DirectIo)
        {
            // Обход кэша допустим только при выравненном по сектору размере
            // буфера. Иначе Windows возвращает ошибку, и копирование падает.
            if (BufferSizeBytes % SectorSize == 0)
                opts |= FileOptions.WriteThrough;
        }
        return opts;
    }

    /// <summary>Человекочитаемая сводка для окна «О программе» и настроек.</summary>
    public static string Describe()
    {
        return $"Буфер: {BufferSizeBytes / 1024} КБ" +
               $" | Потоки: {Concurrency}" +
               $" | Последовательное чтение: {(SequentialScan ? "вкл" : "выкл")}" +
               $" | Direct I/O: {(DirectIo ? "вкл" : "выкл")}" +
               $" | CRC-32 после копирования: {(VerifyCrc32 ? "вкл" : "выкл")}";
    }
}
