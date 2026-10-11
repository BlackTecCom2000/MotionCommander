using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Win11CopyDialog.Modules.StorageControlCenter.Services;

/// <summary>
/// Низкоуровневое чтение настоящей таблицы S.M.A.R.T. напрямую с диска.
///
/// <para><b>Почему это вообще нужно.</b> На этой машине проверено вручную:
/// класс <c>MSFT_StorageReliabilityCounter</c> в пространстве
/// <c>root\Microsoft\Windows\storage</c> присутствует, но возвращает
/// НОЛЬ экземпляров, а классы <c>MSStorageDriver_FailurePredictData</c> в
/// <c>root\wmi</c> вообще отсутствуют. То есть штатных источников S.M.A.R.T.
/// на этой системе нет, и приложение честно показывало «н/д».</para>
///
/// <para>Единственный источник правды — сам диск. Данные читаются командой
/// S.M.A.R.T. через DeviceIoControl. Ничего не вычисляется и не
/// подставляется: если команда не вернулась, результата нет.</para>
///
/// <para><b>Требуются права администратора.</b> Без повышения
/// <c>\\.\PhysicalDriveN</c> не открывается вовсе (проверено: «Отказано в
/// доступе»). Поэтому чтение честно возвращает признак недоступности, а
/// приложение предлагает перезапуск с повышенными правами.</para>
/// </summary>
public static class SmartRawReader
{
    // ===================== IOCTL и структуры =====================

    /// <summary>Чтение S.M.A.R.T. через SCSI-проход (работает для ATA и NVMe).</summary>
    private const uint IOCTL_SCSI_PASS_THROUGH_DIRECT = 0x4D014;

    /// <summary>Вариант для ATA через устаревший путь SMART_RCV_DRIVE_DATA.</summary>
    private const uint SMART_RCV_DRIVE_DATA = 0x00C08820;

    private const int IOCTL_SCSI_MINIPORT = 0x4D048;
    private const int IOCTL_SCSI_PASS_THROUGH = 0x4D004;
    private const int IOCTL_IDE_PASS_THROUGH = 0x4D02C;

    // Коды ATA-команд
    private const byte ATA_CMD_ID = 0xEC;          // IDENTIFY DEVICE
    private const byte ATA_CMD_SMART = 0xB0;       // SMART (feature: 0xD0 read, 0x00 status)
    private const byte ATA_FEATURE_SMART_READ = 0xD0;
    private const byte ATA_FEATURE_SMART_RETURN = 0x00;

    /// <summary>Время ожидания команды, мс. Заведомо с запасом для USB-дисков.</summary>
    private const uint TimeoutMs = 10_000;

    [StructLayout(LayoutKind.Sequential)]
    private struct SCSI_PASS_DIRECT
    {
        public ushort Length;
        public byte ScsiStatus;
        public byte PathId;
        public byte TargetId;
        public byte LunId;
        public byte CdbLength;
        public byte SenseInfoLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] DataIn;          // буфер данных
        public uint DataTransferLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] SenseInfoOffset;
        public byte[] DataBuffer;      // реальный буфер, выделяется отдельно
        public uint SenseInfoLength2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SCSI_PASS
    {
        public ushort Length;
        public byte ScsiStatus;
        public byte PathId;
        public byte TargetId;
        public byte LunId;
        public byte CdbLength;
        public byte SenseInfoLength;
        public uint DataTransferLength;
        public uint TimeOutValue;
        public IntPtr DataBufferPtr;
        public uint SenseInfoOffset;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] Cdb;
        public ushort SenseInfoLength2;
    }

    // Смещение данных в выходном буфере: после cBufferSize (4 байта) и
    // DRIVERSTATUS (12 байт) начинается полезная нагрузка.
    // По спецификации: sizeof(SENDCMDOUTPARAMS) - 1 = 4 + 12 = 16.
    private const int OutputDataOffset = 16;
    private const int SmartDataSize = 512;

    /// <summary>
    /// Регистры IDE-контроллера (IDEREGS из ntdddisk.h).
    /// </summary>
    /// <remarks>
    /// <para>Структура взята из документации Microsoft по
    /// <c>IDEREGS</c>: восемь байтов, каждый — свой регистр. Раньше здесь
    /// стоял самодельный <c>IDE_IDE_FUNCTION</c> с полями Reserved/Count и
    /// указателем на буфер. Такой гибрид не является ни одной из
    /// документированных структур, и драйвер возвращал бы ошибку даже при
    /// полных правах. Для SMART_RCV_DRIVE_DATA входом является именно
    /// SENDCMDINPARAMS с IDEREGS внутри.</para>
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct IdeRegs
    {
        public byte FeaturesReg;     // bFeaturesReg
        public byte SectorCountReg;  // bSectorCountReg
        public byte SectorNumberReg; // bSectorNumberReg
        public byte CylLowReg;       // bCylLowReg
        public byte CylHighReg;      // bCylHighReg
        public byte DriveHeadReg;    // bDriveHeadReg
        public byte CommandReg;      // bCommandReg
        public byte Reserved;        // bReserved, всегда 0
    }

    /// <summary>
    /// Входные параметры SMART_RCV_DRIVE_DATA (SENDCMDINPARAMS).
    /// </summary>
    /// <remarks>
    /// Размер до <c>bBuffer[1]</c> равен 4 + 8 + 1 + 3 + 16 = 32 байта;
    /// столько и требуется передать как входной буфер.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct SendCmdInParams
    {
        public uint BufferSize;         // cBufferSize
        public IdeRegs IrDriveRegs;     // irDriveRegs
        public byte DriveNumber;        // bDriveNumber, драйвер игнорирует
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        public byte[] Reserved;         // bReserved[3]
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public uint[] ReservedDwords;   // dwReserved[4]
    }

    /// <summary>Состояние драйвера в ответе (DRIVERSTATUS).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct DriverStatus
    {
        public byte DriverError;        // bDriverError
        public byte IdeError;           // bIDEError
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
        public byte[] Reserved;         // bReserved[2]
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
        public uint[] ReservedDwords;   // dwReserved[2]
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        IntPtr hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    // ===================== Результат чтения =====================

    /// <summary>Сырой блок S.M.A.R.T. с одного диска: 30 атрибутов ATA или NVMe-данные.</summary>
    public sealed class RawSmartData
    {
        /// <summary>512 байт: для ATA это таблица атрибутов, для NVMe — лог SMART/Health.</summary>
        public byte[] Data512 { get; init; } = new byte[512];

        /// <summary>Общая оценка S.M.A.R.T. (самотест пройден или нет).</summary>
        public bool OverallPass { get; set; }

        /// <summary>Настоящий ли это S.M.A.R.T. (а не нули от заглушки контроллера).</summary>
        public bool IsPlausible { get; set; }

        /// <summary>Являются ли прочитанные данные блоком NVMe Health Log (иначе ATA SMART).</summary>
        public bool IsNvme { get; set; }

        public string ModelFromIdentify { get; set; } = "";
        public string SerialFromIdentify { get; set; } = "";
        public string FirmwareFromIdentify { get; set; } = "";
    }

    /// <summary>
    /// Причина, по которой чтение не удалось. Нужна пользователю: молча
    /// показать «н/д» значит скрыть, что именно мешает.
    /// </summary>
    public sealed class SmartUnavailable
    {
        public string Reason { get; init; } = "";
        public bool NeedsAdministrator { get; init; }
        public int Win32ErrorCode { get; init; }
        public string MethodAttempted { get; init; } = "";
    }

    // ===================== Чтение =====================

    /// <summary>
    /// Открывает физический диск. Возвращает null при отказе в доступе —
    /// это отдельный случай, который вызывающий код обязан отличать от
    /// «диск не поддерживает S.M.A.R.T.».
    /// </summary>
    private static DiskHandle? OpenDrive(int diskNumber, out SmartUnavailable? failure, bool writeAccess = true)
    {
        failure = null;
        string path = $@"\\.\PhysicalDrive{diskNumber}";

        const uint GENERIC_READ = 0x80000000;
        const uint GENERIC_WRITE = 0x40000000;
        const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
        const uint OPEN_EXISTING = 3;

        uint desiredAccess = writeAccess ? (GENERIC_READ | GENERIC_WRITE) : 0;
        var handle = CreateFileW(path, desiredAccess,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

        // INVALID_HANDLE_VALUE = -1, это признак ошибки, а не успеха.
        if (handle != IntPtr.Zero && handle != new IntPtr(-1))
            return new DiskHandle(handle);

        int err = Marshal.GetLastWin32Error();

        // Если запрошен полный доступ и получен отказ, пробуем режим запроса свойств (access 0)
        if (writeAccess)
        {
            handle = CreateFileW(path, 0,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle != IntPtr.Zero && handle != new IntPtr(-1))
                return new DiskHandle(handle);
            err = Marshal.GetLastWin32Error();
        }

        if (err == 5) // ERROR_ACCESS_DENIED
        {
            failure = new SmartUnavailable
            {
                Reason = $"Накопитель #{diskNumber} доступен только с правами администратора (Win32 код 5: Access Denied).",
                NeedsAdministrator = true,
                Win32ErrorCode = 5,
                MethodAttempted = "CreateFileW(PhysicalDrive)"
            };
            return null;
        }

        failure = new SmartUnavailable
        {
            Reason = err == 2
                ? $"Устройство PhysicalDrive{diskNumber} не найдено."
                : $"Не удалось открыть накопитель PhysicalDrive{diskNumber} (код ошибки Windows {err}).",
            Win32ErrorCode = err,
            MethodAttempted = "CreateFileW(PhysicalDrive)"
        };
        return null;
    }

    /// <summary>
    /// Освобождаемый дескриптор устройства.
    /// </summary>
    /// <remarks>
    /// Раньше здесь стоял самодельный класс, производный от выдуманного
    /// «SafeHandleZeroOrMinusOneIsInvalid», которого нет ни в .NET, ни в
    /// Windows Forms: сборка падала с «не удаётся найти тип». Теперь
    /// используется штатный <see cref="SafeFileHandle"/>, который сам знает,
    /// что INVALID_HANDLE_VALUE — это ошибка.
    /// </remarks>
    private sealed class DiskHandle : IDisposable
    {
        /// <summary>
        /// Сырой дескриптор для DeviceIoControl. Именно IntPtr, а не
        /// SafeFileHandle: P/Invoke ожидает обычный HANDLE.
        /// </summary>
        public IntPtr HandleValue { get; }

        public bool IsValid => HandleValue != IntPtr.Zero && HandleValue != new IntPtr(-1);

        public DiskHandle(IntPtr h) => HandleValue = h;

        public void Dispose()
        {
            if (IsValid) CloseHandle(HandleValue);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }

    // ===================== ATA: чтение через SMART_RCV_DRIVE_DATA =====================

    /// <summary>
    /// Отправляет S.M.A.R.T.-команду накопителю и возвращает ответ.
    /// </summary>
    /// <remarks>
    /// <para>Регистры заполняются по требованиям ATA для команды SMART
    /// (0xB0) в режиме LBA: обязателен «волшебный» адрес сектора
    /// 0x4F/0xC2/0xC2 и старший бит 0xA0 в регистре привода. Без них
    /// контроллер отвечает ошибкой и никаких данных не возвращает.</para>
    /// </remarks>
    private static bool SendSmartCommand(
        DiskHandle drive, byte feature, byte[] outData, out int driverError)
    {
        driverError = 0;

        var input = new SendCmdInParams
        {
            BufferSize = SmartDataSize,
            IrDriveRegs = new IdeRegs
            {
                FeaturesReg = feature,
                SectorCountReg = 0x01,
                SectorNumberReg = 0x4F,
                CylLowReg = 0xC2,
                CylHighReg = 0xC2,
                DriveHeadReg = 0xA0,
                CommandReg = ATA_CMD_SMART,
                Reserved = 0
            },
            DriveNumber = 0,
            Reserved = new byte[3],
            ReservedDwords = new uint[4]
        };

        int inSize = Marshal.SizeOf<SendCmdInParams>();
        // Выход должен быть не меньше sizeof(SENDCMDOUTPARAMS)-1 + 512.
        int outSize = OutputDataOffset + SmartDataSize;

        IntPtr inBuf = Marshal.AllocHGlobal(inSize);
        IntPtr outBuf = Marshal.AllocHGlobal(outSize);
        try
        {
            Marshal.StructureToPtr(input, inBuf, false);
            ZeroBuffer(outBuf, outSize);

            uint returned;
            bool ok = DeviceIoControl(drive.HandleValue, SMART_RCV_DRIVE_DATA,
                inBuf, (uint)inSize, outBuf, (uint)outSize, out returned, IntPtr.Zero);

            if (!ok) return false;

            var status = Marshal.PtrToStructure<DriverStatus>(outBuf + 4);
            driverError = status.DriverError;
            if (status.DriverError != 0) return false;

            Marshal.Copy(outBuf + OutputDataOffset, outData, 0, SmartDataSize);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(inBuf);
            Marshal.FreeHGlobal(outBuf);
        }
    }

    /// <summary>
    /// Обнуление буфера перед DeviceIoControl.
    /// </summary>
    /// <remarks>
    /// Выходной буфер обязан быть чистым: драйвер дописывает в него 512
    /// байт данных, и мусор в хвосте выглядел бы как прочитанные
    /// атрибуты. Раньше здесь стояли две декларации одного и того же
    /// метода — одна с несуществующим атрибутом, другая с EntryPoint,
    /// указывающим на экспорт kernel32, которого там нет.
    /// </remarks>
    private static void ZeroBuffer(IntPtr buffer, int length)
    {
        for (int i = 0; i < length; i++)
            Marshal.WriteByte(buffer, i, 0);
    }

    /// <summary>Читает настоящую таблицу S.M.A.R.T. через ATA-путь.</summary>
    public static RawSmartData? ReadAtaSmart(int diskNumber, out SmartUnavailable? failure)
    {
        failure = null;
        var handle = OpenDrive(diskNumber, out failure);
        if (handle == null) return null;

        try
        {
            // 1. Таблица атрибутов: SMART READ DATA (features = 0xD0).
            //    Это основной источник: из него берутся температура, износ,
            //    наработка и состояние секторов.
            var result = new RawSmartData();
            var scratch = new byte[SmartDataSize];

            if (!SendSmartCommand(handle, ATA_FEATURE_SMART_READ, scratch, out int readError))
            {
                failure = DescribeReadFailure(readError, diskNumber);
                return null;
            }

            Array.Copy(scratch, result.Data512, SmartDataSize);

            // 2. Итог самотеста: SMART RETURN STATUS (features = 0x00).
            //    Отдельная команда, потому что атрибуты её не содержат.
            var status = new byte[SmartDataSize];
            result.OverallPass = SendSmartCommand(handle, ATA_FEATURE_SMART_RETURN, status, out _);

            result.IsPlausible = SmartRawParser.HasPlausibleAttributes(result.Data512);
            return result;
        }
        catch (Exception ex)
        {
            failure = new SmartUnavailable { Reason = $"Ошибка чтения S.M.A.R.T.: {ex.Message}" };
            return null;
        }
        finally
        {
            handle.Dispose();
        }
    }

    /// <summary>
    /// Объясняет причину неудачи максимально конкретно.
    /// </summary>
    /// <remarks>
    /// Общее сообщение «накопитель не поддерживает S.M.A.R.T.» обманывало
    /// бы пользователя: на этой машине причина в другом — контроллер
    /// не отдаёт предиктивные данные. Разные причины требуют разных
    /// действий, поэтому сообщение строятся по коду ошибки драйвера.
    /// </remarks>
    private static SmartUnavailable DescribeReadFailure(int driverError, int diskNumber)
    {
        // bDriverError: 0x07 и 0x0B означают, что контроллер не поддерживает
        // SMART-команду (типично для NVMe через ATA-совместимый слой и RAID).
        if (driverError is 0x07 or 0x0B)
        {
            return new SmartUnavailable
            {
                Reason = $"Контроллер накопителя #{diskNumber} не поддерживает команду S.M.A.R.T. " +
                         "или не отдаёт предиктивные данные. Для NVMe-дисков используется другой протокол."
            };
        }

        if (driverError == 0x1D)
        {
            return new SmartUnavailable
            {
                Reason = $"Накопитель #{diskNumber} вернул ошибку команды: параметры S.M.A.R.T. отклонены драйвером."
            };
        }

        if (driverError != 0)
        {
            return new SmartUnavailable
            {
                Reason = $"Накопитель #{diskNumber} вернул ошибку драйвера 0x{driverError:X2} при чтении S.M.A.R.T."
            };
        }

        return new SmartUnavailable
        {
            Reason = $"Накопитель #{diskNumber} не вернул данные S.M.A.R.T."
        };
    }

    // ===================== NVMe: чтение через IOCTL_STORAGE_QUERY_PROPERTY =====================

    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    private const uint StorageAdapterProtocolSpecificProperty = 49;
    private const uint StorageDeviceProtocolSpecificProperty = 50;
    private const uint PropertyStandardQuery = 0;
    private const uint ProtocolTypeNvme = 2;
    private const uint NVMeDataTypeLogPage = 2;
    private const uint NVME_LOG_PAGE_HEALTH_INFO = 0x02;

    [StructLayout(LayoutKind.Sequential)]
    private struct StorageProtocolSpecificQuery
    {
        public uint PropertyId;
        public uint QueryType;
        public uint ProtocolType;
        public uint DataType;
        public uint ProtocolDataRequestValue;
        public uint ProtocolDataRequestSubValue;
        public uint ProtocolDataOffset;
        public uint ProtocolDataLength;
        public uint FixedProtocolReturnData;
        public uint FixedProtocolReturnDataSubValue;
        public uint AdditionalDataLength;
        public uint Reserved;
    }

    /// <summary>
    /// Читает NVMe S.M.A.R.T./Health Information Log (512 байт).
    /// </summary>
    public static RawSmartData? ReadNvmeSmart(int diskNumber, out SmartUnavailable? failure)
    {
        failure = null;
        var handle = OpenDrive(diskNumber, out failure, writeAccess: false);
        if (handle == null) return null;

        try
        {
            // Пробуем DeviceProtocolSpecific (50), затем AdapterProtocolSpecific (49)
            uint[] propertyIds = { StorageDeviceProtocolSpecificProperty, StorageAdapterProtocolSpecificProperty };
            int lastErr = 0;

            foreach (uint propId in propertyIds)
            {
                var data = QueryNvmeHealthLog(handle.HandleValue, propId, out lastErr);
                if (data != null && HasPlausibleNvmeData(data))
                {
                    return new RawSmartData
                    {
                        Data512 = data,
                        IsNvme = true,
                        OverallPass = (data[0] & 0x05) == 0, // CriticalWarning bits 0 (spare) & 2 (reliability)
                        IsPlausible = true
                    };
                }
            }

            failure = new SmartUnavailable
            {
                Reason = $"NVMe Health Log накопителя #{diskNumber} не ответил на стандартные запросы IOCTL (Win32 код {lastErr}).",
                Win32ErrorCode = lastErr,
                MethodAttempted = "IOCTL_STORAGE_QUERY_PROPERTY(NVMe)"
            };
            return null;
        }
        catch (Exception ex)
        {
            failure = new SmartUnavailable
            {
                Reason = $"Сбой при чтении NVMe S.M.A.R.T. накопителя #{diskNumber}: {ex.Message}",
                MethodAttempted = "ReadNvmeSmart"
            };
            return null;
        }
        finally
        {
            handle.Dispose();
        }
    }

    private static byte[]? QueryNvmeHealthLog(IntPtr hDevice, uint propertyId, out int win32Error)
    {
        win32Error = 0;
        int inSize = Marshal.SizeOf<StorageProtocolSpecificQuery>();
        var query = new StorageProtocolSpecificQuery
        {
            PropertyId = propertyId,
            QueryType = PropertyStandardQuery,
            ProtocolType = ProtocolTypeNvme,
            DataType = NVMeDataTypeLogPage,
            ProtocolDataRequestValue = NVME_LOG_PAGE_HEALTH_INFO,
            ProtocolDataRequestSubValue = 0,
            ProtocolDataOffset = 40,
            ProtocolDataLength = 512,
            FixedProtocolReturnData = 0,
            FixedProtocolReturnDataSubValue = 0,
            AdditionalDataLength = 0,
            Reserved = 0
        };

        int outSize = 4096;
        IntPtr inPtr = Marshal.AllocHGlobal(inSize);
        IntPtr outPtr = Marshal.AllocHGlobal(outSize);
        try
        {
            Marshal.StructureToPtr(query, inPtr, false);
            ZeroBuffer(outPtr, outSize);

            bool ok = DeviceIoControl(hDevice, IOCTL_STORAGE_QUERY_PROPERTY,
                inPtr, (uint)inSize, outPtr, (uint)outSize, out uint bytesReturned, IntPtr.Zero);

            if (!ok || bytesReturned < 48)
            {
                win32Error = Marshal.GetLastWin32Error();
                return null;
            }

            // Читаем смещение полезных данных из дескриптора
            uint protocolDataOffset = (uint)Marshal.ReadInt32(outPtr, 24);
            int dataStart = (int)protocolDataOffset;
            if (dataStart < 40 || dataStart + 512 > bytesReturned)
            {
                dataStart = 48; // Размер стандартного заголовка
            }

            if (dataStart + 512 <= bytesReturned)
            {
                byte[] data = new byte[512];
                Marshal.Copy(outPtr + dataStart, data, 0, 512);
                return data;
            }

            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(inPtr);
            Marshal.FreeHGlobal(outPtr);
        }
    }

    public static bool HasPlausibleNvmeData(byte[] log)
    {
        if (log == null || log.Length < 512) return false;

        ushort kelvin = (ushort)(log[1] | (log[2] << 8));
        double tempC = kelvin > 0 ? (kelvin - 273.15) : 0;
        bool plausibleTemp = tempC is >= 5 and <= 120;

        byte spare = log[3];
        byte wear = log[5];

        bool hasNonZero = false;
        for (int i = 0; i < 64; i++)
        {
            if (log[i] != 0) { hasNonZero = true; break; }
        }

        return hasNonZero && (plausibleTemp || (spare <= 100 && wear <= 200));
    }

    // ===================== Публичный вход =====================

    /// <summary>
    /// Читает S.M.A.R.T. с указанного накопителя (NVMe либо ATA).
    /// Возвращает null, если данных нет, и заполняет failure причиной.
    /// </summary>
    public static RawSmartData? Read(int diskNumber, out SmartUnavailable? failure)
    {
        failure = null;

        // 1. Сначала пробуем протокол NVMe (быстро и нативно для SSD PCIe)
        var nvme = ReadNvmeSmart(diskNumber, out var nvmeFail);
        if (nvme != null && nvme.IsPlausible)
            return nvme;

        // 2. Если не NVMe — пробуем ATA S.M.A.R.T. команду
        var ata = ReadAtaSmart(diskNumber, out var ataFail);
        if (ata != null && ata.IsPlausible)
            return ata;

        // 3. Если отказ в правах администратора — фиксируем его явно
        if (nvmeFail?.NeedsAdministrator == true || ataFail?.NeedsAdministrator == true)
        {
            failure = new SmartUnavailable
            {
                Reason = $"Накопитель #{diskNumber} требует прав администратора для прямого чтения S.M.A.R.T.",
                NeedsAdministrator = true,
                Win32ErrorCode = 5,
                MethodAttempted = "DirectIOCTL(NVMe/ATA)"
            };
            return null;
        }

        failure = ataFail ?? nvmeFail ?? new SmartUnavailable
        {
            Reason = $"Накопитель #{diskNumber} не предоставил данные S.M.A.R.T. (не поддерживается контроллером или виртуальный диск)."
        };
        return null;
    }
}

/// <summary>
/// Разбор блока S.M.A.R.T. в понятные значения.
/// </summary>
/// <remarks>
/// Блок 512 байт разбит на 30 записей по 12 байт плюс служебный блок.
/// Разметка записи (ATA-спецификация):
/// <list type="bullet">
/// <item>0 — идентификатор атрибута</item>
/// <item>1..2 — флаги, устаревшие и всегда 0xF8</item>
/// <item>3 — текущее нормализованное значение (1..253, больше значит лучше)</item>
/// <item>4 — худшее за всё время</item>
/// <item>5 — порог, ниже которого атрибут считается критическим</item>
/// <item>6..11 — сырое значение, младшие байты идут первыми</item>
/// </list>
/// </remarks>
public static class SmartRawParser
{
    /// <summary>
    /// Отбрасывает заглушки. Многие RAID-контроллеры и виртуальные диски
    /// возвращают 512 нулей вместо настоящей таблицы. Такие данные
    /// показывать нельзя: это выглядело бы как «диск в порядке», хотя
    /// ничего не измерено.
    /// </summary>
    public static bool HasPlausibleAttributes(byte[] block)
    {
        if (block == null || block.Length < 12) return false;

        int valid = 0;
        int nonZeroRaw = 0;
        for (int offset = 0; offset + 11 < block.Length && offset < 30 * 12; offset += 12)
        {
            byte id = block[offset];
            if (id == 0) continue;

            // Валидный атрибут: текущее значение 1..253 и флаги 0xF8.
            byte current = block[offset + 3];
            if (current == 0 || current == 0xFF) continue;
            valid++;

            // Смещение offset обязательно: сырое значение лежит в блоке
            // [offset+6 .. offset+11] своего атрибута. Без него читались
            // байты ПЕРВОГО атрибута, из-за чего проверка правдоподобности
            // принимала или отвергала таблицу по чужим данным.
            long raw = 0;
            for (int b = 11; b >= 6; b--) raw = (raw << 8) | block[offset + b];
            if (raw > 0) nonZeroRaw++;
        }

        // Нужно минимум несколько настоящих атрибутов, иначе это пустышка.
        return valid >= 3 && (nonZeroRaw >= 2 || valid >= 5);
    }
}
