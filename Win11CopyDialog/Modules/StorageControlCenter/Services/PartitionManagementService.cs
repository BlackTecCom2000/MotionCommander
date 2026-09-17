using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Win11CopyDialog.Modules.StorageControlCenter.Models;

namespace Win11CopyDialog.Modules.StorageControlCenter.Services;

/// <summary>
/// Профессиональный движок управления дисками и разделами уровня Super-Admin (Disk Partition Manager Engine).
/// Использует прямой вызов нативного низкоуровневого инструмента DiskPart с флагом OVERRIDE,
/// что принудительно обходит любые системные блокировки OEM, Recovery, GPT attribute bits и занятости томов.
/// </summary>
public static class PartitionManagementService
{
    public static readonly List<StorageOperationLog> OperationLogs = new();

    static PartitionManagementService()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch { }
    }

    /// <summary>
    /// Аппаратная и программная проверка безопасности. Категорически блокирует удаление или форматирование диска C:.
    /// </summary>
    public static void ValidateSafeTarget(StoragePartition partition, string operationName)
    {
        if (partition.IsSystem || partition.IsBoot || partition.DriveLetter.Equals("C", StringComparison.OrdinalIgnoreCase))
        {
            LogAction(operationName, $"{partition.DriveLetter}: ({partition.DisplayName})", StorageRiskLevel.IRREVERSIBLE, "Заблокировано защитой", "Попытка модификации защищенного системного раздела Windows");
            throw new InvalidOperationException($"Безопасность системы: операция «{operationName}» категорически заблокирована для системного тома {partition.DriveLetter}: во избежание повреждения работающей операционной системы Windows.");
        }
    }

    /// <summary>
    /// Аппаратная и программная проверка безопасности всего диска целиком.
    /// Блокирует любые деструктивные операции на диске, если он содержит системный том.
    /// </summary>
    public static void ValidateSafeTargetDisk(StorageDisk disk, string operationName, MigrationPlan plan = null)
    {
        if (disk.Partitions.Any(p => p.IsSystem || p.IsBoot || p.DriveLetter.Equals("C", StringComparison.OrdinalIgnoreCase)))
        {
            LogAction(operationName, $"{disk.Model} (Диск {disk.DiskNumber})", StorageRiskLevel.IRREVERSIBLE, "Заблокировано защитой", "Попытка модификации диска, содержащего работающую ОС Windows");
            throw new InvalidOperationException($"Безопасность системы: операция «{operationName}» категорически заблокирована для диска {disk.DiskNumber}, так как он является системным диском текущей Windows.");
        }

        if (plan != null && plan.IsDestructive && !plan.UserConfirmedOverride)
        {
            LogAction(operationName, $"{disk.Model}", StorageRiskLevel.IRREVERSIBLE, "Заблокировано защитой", "Отсутствует явное подтверждение пользователя для деструктивной операции");
            throw new InvalidOperationException($"Безопасность системы: операция «{operationName}» требует явного подтверждения пользователя (UserConfirmedOverride), так как она приведет к полной потере данных на диске.");
        }
    }

    /// <summary>
    /// Принудительное удаление любого раздела с флагом OVERRIDE (снимает защиту с OEM/Recovery/GPT атрибутов).
    /// </summary>
    public static async Task<(bool success, string message)> DeletePartitionAsync(StoragePartition partition, bool forceOverride = true, CancellationToken ct = default)
    {
        ValidateSafeTarget(partition, "Удаление раздела");

        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(partition.DriveLetter))
        {
            sb.AppendLine($"select volume {partition.DriveLetter}");
            sb.AppendLine($"remove letter={partition.DriveLetter}");
        }
        sb.AppendLine($"select disk {partition.DiskNumber}");
        sb.AppendLine($"select partition {partition.PartitionNumber}");
        sb.AppendLine(forceOverride ? "delete partition override" : "delete partition");

        var res = await RunDiskPartAsync(sb.ToString(), ct);

        // Fallback на PowerShell CIM если DiskPart вернул специфичную ошибку
        if (!res.success)
        {
            string psScript = $"Remove-Partition -DiskNumber {partition.DiskNumber} -PartitionNumber {partition.PartitionNumber} -Confirm:$false";
            var psRes = await RunPowerShellAsync(psScript, ct);
            if (psRes.success)
            {
                res = (true, psRes.output);
            }
        }

        string cleanMsg = CleanOutput(res.output);
        LogAction("Удаление раздела (Override)", $"{partition.DisplayName} (Диск {partition.DiskNumber}, Том {partition.PartitionNumber})", 
            StorageRiskLevel.DESTRUCTIVE, res.success ? "Удален" : "Ошибка", cleanMsg);

        return (res.success, cleanMsg);
    }

    /// <summary>
    /// Создание нового раздела на нераспределенном дисковом пространстве.
    /// </summary>
    public static async Task<(bool success, string message)> CreatePartitionAsync(
        int diskNumber, 
        long sizeBytes, 
        string fs = "NTFS", 
        string label = "Новый том", 
        char? driveLetter = null, 
        int clusterSize = 4096, 
        CancellationToken ct = default)
    {
        long sizeMB = sizeBytes / (1024 * 1024);
        var sb = new StringBuilder();
        sb.AppendLine($"select disk {diskNumber}");

        if (sizeMB > 0)
        {
            sb.AppendLine($"create partition primary size={sizeMB}");
        }
        else
        {
            sb.AppendLine("create partition primary");
        }

        string clusterParam = clusterSize > 0 ? $"unit={clusterSize}" : "";
        sb.AppendLine($"format fs={fs} label=\"{label}\" quick {clusterParam}".Trim());

        if (driveLetter.HasValue && char.IsLetter(driveLetter.Value))
        {
            sb.AppendLine($"assign letter={char.ToUpper(driveLetter.Value)}");
        }
        else
        {
            sb.AppendLine("assign");
        }

        var res = await RunDiskPartAsync(sb.ToString(), ct);
        string cleanMsg = CleanOutput(res.output);

        LogAction("Создание раздела", $"Диск {diskNumber}, {sizeMB} МБ [{fs}] «{label}»", 
            StorageRiskLevel.CAUTION, res.success ? "Создан" : "Ошибка", cleanMsg);

        return (res.success, cleanMsg);
    }

    /// <summary>
    /// Сжатие раздела (Shrink) для высвобождения нераспределенного места.
    /// </summary>
    public static async Task<(bool success, string message)> ShrinkPartitionAsync(StoragePartition partition, long shrinkBytes, CancellationToken ct = default)
    {
        ValidateSafeTarget(partition, "Сжатие раздела");

        long shrinkMB = shrinkBytes / (1024 * 1024);
        if (shrinkMB <= 0) shrinkMB = 1024; // По умолчанию 1 ГБ

        var sb = new StringBuilder();
        sb.AppendLine($"select disk {partition.DiskNumber}");
        sb.AppendLine($"select partition {partition.PartitionNumber}");
        sb.AppendLine($"shrink desired={shrinkMB}");

        var res = await RunDiskPartAsync(sb.ToString(), ct);
        string cleanMsg = CleanOutput(res.output);

        LogAction("Сжатие тома", $"{partition.DisplayName} на {shrinkMB} МБ", 
            StorageRiskLevel.CAUTION, res.success ? "Сжат" : "Ошибка", cleanMsg);

        return (res.success, cleanMsg);
    }

    /// <summary>
    /// Расширение раздела (Extend) за счет смежного нераспределенного пространства.
    /// </summary>
    public static async Task<(bool success, string message)> ExtendPartitionAsync(StoragePartition partition, long extendBytes = 0, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"select disk {partition.DiskNumber}");
        sb.AppendLine($"select partition {partition.PartitionNumber}");

        long extendMB = extendBytes / (1024 * 1024);
        if (extendMB > 0)
        {
            sb.AppendLine($"extend size={extendMB}");
        }
        else
        {
            sb.AppendLine("extend");
        }

        var res = await RunDiskPartAsync(sb.ToString(), ct);
        string cleanMsg = CleanOutput(res.output);

        LogAction("Расширение тома", $"{partition.DisplayName} {(extendMB > 0 ? $"+{extendMB} МБ" : "(Максимум)")}", 
            StorageRiskLevel.CAUTION, res.success ? "Расширен" : "Ошибка", cleanMsg);

        return (res.success, cleanMsg);
    }

    /// <summary>
    /// Форматирование раздела с выбором файловой системы и размера кластера.
    /// </summary>
    public static async Task<(bool success, string message)> FormatPartitionAsync(
        StoragePartition partition, 
        string fs = "NTFS", 
        string label = "Локальный диск", 
        bool quick = true, 
        int clusterSize = 4096, 
        CancellationToken ct = default)
    {
        ValidateSafeTarget(partition, "Форматирование раздела");

        var sb = new StringBuilder();
        sb.AppendLine($"select disk {partition.DiskNumber}");
        sb.AppendLine($"select partition {partition.PartitionNumber}");

        string quickParam = quick ? "quick" : "";
        string clusterParam = clusterSize > 0 ? $"unit={clusterSize}" : "";
        sb.AppendLine($"format fs={fs} label=\"{label}\" {quickParam} {clusterParam}".Trim());

        var res = await RunDiskPartAsync(sb.ToString(), ct);
        string cleanMsg = CleanOutput(res.output);

        LogAction("Форматирование тома", $"{partition.DisplayName} [{fs}] «{label}»", 
            StorageRiskLevel.DESTRUCTIVE, res.success ? "Успешно" : "Ошибка", cleanMsg);

        return (res.success, cleanMsg);
    }

    /// <summary>
    /// Смена или назначение буквы диска. Если newLetter == '\0' — буква удаляется (скрытие тома).
    /// </summary>
    public static async Task<(bool success, string message)> ChangeDriveLetterAsync(int diskNumber, int partitionNumber, char newLetter, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"select disk {diskNumber}");
        sb.AppendLine($"select partition {partitionNumber}");

        if (char.IsLetter(newLetter))
        {
            sb.AppendLine($"assign letter={char.ToUpper(newLetter)}");
        }
        else
        {
            sb.AppendLine("remove");
        }

        var res = await RunDiskPartAsync(sb.ToString(), ct);
        string cleanMsg = CleanOutput(res.output);

        string actionDesc = char.IsLetter(newLetter) ? $"➔ {char.ToUpper(newLetter)}:" : "Удаление буквы (скрытие)";
        LogAction("Буква диска", $"Диск {diskNumber}, Раздел {partitionNumber} {actionDesc}", 
            StorageRiskLevel.SAFE, res.success ? "Успешно" : "Ошибка", cleanMsg);

        return (res.success, cleanMsg);
    }

    /// <summary>
    /// Смена метки тома.
    /// </summary>
    public static async Task<(bool success, string message)> ChangeVolumeLabelAsync(string driveLetter, string newLabel, CancellationToken ct = default)
    {
        string letter = driveLetter.TrimEnd('\\', ':');
        string script = $"Set-Volume -DriveLetter '{letter}' -NewFileSystemLabel '{newLabel}'";
        var res = await RunPowerShellAsync(script, ct);
        string cleanMsg = CleanOutput(res.output);

        LogAction("Смена метки тома", $"{letter}: ➔ «{newLabel}»", StorageRiskLevel.SAFE, res.success ? "Успешно" : "Ошибка", cleanMsg);
        return (res.success, cleanMsg);
    }

    /// <summary>
    /// Полная очистка диска (Wipe/Clean Partition Table) через DiskPart Clean.
    /// </summary>
    public static async Task<(bool success, string message)> CleanDiskAsync(StorageDisk disk, CancellationToken ct = default)
    {
        // Проверка: содержит ли данный диск системный том C:
        ValidateSafeTargetDisk(disk, "Очистка диска (Clean)");

        var sb = new StringBuilder();
        sb.AppendLine($"select disk {disk.DiskNumber}");
        sb.AppendLine("clean");

        var res = await RunDiskPartAsync(sb.ToString(), ct);
        string cleanMsg = CleanOutput(res.output);

        LogAction("Очистка диска (Clean)", $"{disk.Model} (Диск {disk.DiskNumber})", 
            StorageRiskLevel.IRREVERSIBLE, res.success ? "Очищен" : "Ошибка", cleanMsg);

        return (res.success, cleanMsg);
    }

    /// <summary>
    /// Снятие атрибута «Только чтение» (Clear Read-Only) с диска и разделов.
    /// </summary>
    public static async Task<(bool success, string message)> ClearReadOnlyAsync(int diskNumber, int? partitionNumber = null, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"select disk {diskNumber}");
        sb.AppendLine("attributes disk clear readonly");
        if (partitionNumber.HasValue)
        {
            sb.AppendLine($"select partition {partitionNumber.Value}");
            sb.AppendLine("attributes volume clear readonly");
        }

        var res = await RunDiskPartAsync(sb.ToString(), ct);
        string cleanMsg = CleanOutput(res.output);

        LogAction("Снятие Read-Only", $"Диск {diskNumber}", StorageRiskLevel.SAFE, res.success ? "Снято" : "Ошибка", cleanMsg);
        return (res.success, cleanMsg);
    }

    /// <summary>
    /// Запуск проверки файловой системы тома (Chkdsk).
    /// </summary>
    public static async Task<(bool success, string message)> CheckFileSystemAsync(string driveLetter, CancellationToken ct = default)
    {
        string letter = driveLetter.TrimEnd('\\', ':');
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "chkdsk.exe",
                Arguments = $"{letter}: /scan",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.GetEncoding(866)
            };

            using var proc = new Process { StartInfo = psi };
            proc.Start();
            string stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            string stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            string output = (stdout + "\n" + stderr).Trim();
            bool success = proc.ExitCode == 0;
            LogAction("Проверка Chkdsk", $"{letter}:", StorageRiskLevel.SAFE, success ? "Исправен" : "Предупреждение", output);
            return (success, output);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Создает структуру разделов для миграции БЕЗ полного удаления других разделов.
    /// Использует неразмеченное пространство (в данной реализации - весь пустой диск без вызова Clean).
    /// </summary>
    public static async Task<(bool success, string output)> CreateSafeOsPartitionAsync(int targetDiskNumber, string partitionStyle = "GPT", CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"select disk {targetDiskNumber}");
        
        if (partitionStyle.Equals("GPT", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine("convert gpt");
            
            // EFI System Partition
            sb.AppendLine("create partition efi size=100");
            sb.AppendLine("format quick fs=fat32 label=\"System\"");
            sb.AppendLine("assign letter=S");

            // Microsoft Reserved Partition
            sb.AppendLine("create partition msr size=16");

            // Windows Partition (uses all remaining unallocated space)
            sb.AppendLine("create partition primary");
            sb.AppendLine("format quick fs=ntfs label=\"Windows\"");
            sb.AppendLine("assign letter=W");
        }
        else
        {
            sb.AppendLine("convert mbr");
            sb.AppendLine("create partition primary");
            sb.AppendLine("format quick fs=ntfs label=\"Windows\"");
            sb.AppendLine("assign letter=W");
            sb.AppendLine("active");
        }

        var res = await RunDiskPartAsync(sb.ToString(), ct);
        string cleanMsg = CleanOutput(res.output);
        
        LogAction("Безопасная разметка", $"Диск {targetDiskNumber} [{partitionStyle}]", StorageRiskLevel.CAUTION, res.success ? "Успешно" : "Ошибка", cleanMsg);
        
        return (res.success, cleanMsg);
    }

    /// <summary>
    /// Деструктивная операция. Уничтожает все данные на целевом диске и создает чистую структуру.
    /// </summary>
    public static async Task<(bool success, string output)> WipeAndCreateSystemPartitionsAsync(StorageDisk targetDisk, MigrationPlan plan, string partitionStyle = "GPT", CancellationToken ct = default)
    {
        ValidateSafeTargetDisk(targetDisk, "Полное клонирование с очисткой", plan);

        var sb = new StringBuilder();
        sb.AppendLine($"select disk {targetDisk.DiskNumber}");
        sb.AppendLine("clean");
        
        if (partitionStyle.Equals("GPT", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine("convert gpt");
            
            // EFI System Partition
            sb.AppendLine("create partition efi size=100");
            sb.AppendLine("format quick fs=fat32 label=\"System\"");
            sb.AppendLine("assign letter=S"); // Временная буква для bcdboot

            // Microsoft Reserved Partition
            sb.AppendLine("create partition msr size=16");

            // Windows Partition
            sb.AppendLine("create partition primary");
            sb.AppendLine("format quick fs=ntfs label=\"Windows\"");
            sb.AppendLine("assign letter=W"); // Временная буква для копирования файлов
        }
        else
        {
            sb.AppendLine("convert mbr");
            sb.AppendLine("create partition primary");
            sb.AppendLine("format quick fs=ntfs label=\"Windows\"");
            sb.AppendLine("assign letter=W");
            sb.AppendLine("active"); // Сделать активным для MBR
        }

        var res = await RunDiskPartAsync(sb.ToString(), ct);
        string cleanMsg = CleanOutput(res.output);
        
        LogAction("Разметка под миграцию", $"Диск {targetDisk.DiskNumber} [{partitionStyle}]", StorageRiskLevel.DESTRUCTIVE, res.success ? "Успешно" : "Ошибка", cleanMsg);
        
        return (res.success, cleanMsg);
    }

    /// <summary>
    /// Выполняет низкоуровневый скрипт DiskPart с декодированием в правильной кодировке.
    /// </summary>
    private static async Task<(bool success, string output)> RunDiskPartAsync(string script, CancellationToken ct)
    {
        string tempScriptPath = Path.Combine(Path.GetTempPath(), $"mc_dp_{Guid.NewGuid():N}.txt");
        try
        {
            // DiskPart принимает ASCII/ANSI текст скрипта
            await File.WriteAllTextAsync(tempScriptPath, script, Encoding.ASCII, ct);

            Encoding oemEncoding;
            try
            {
                oemEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            }
            catch
            {
                oemEncoding = Encoding.GetEncoding(866);
            }

            var psi = new ProcessStartInfo
            {
                FileName = "diskpart.exe",
                Arguments = $"/s \"{tempScriptPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = oemEncoding,
                StandardErrorEncoding = oemEncoding
            };

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            string stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            string stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            string combined = (stdout + "\n" + stderr).Trim();

            // Проверка на ошибки DiskPart
            bool hasError = proc.ExitCode != 0 || 
                            combined.Contains("DiskPart has encountered an error", StringComparison.OrdinalIgnoreCase) ||
                            combined.Contains("Ошибка программы DiskPart", StringComparison.OrdinalIgnoreCase) ||
                            combined.Contains("Отказано в доступе", StringComparison.OrdinalIgnoreCase) ||
                            combined.Contains("Access is denied", StringComparison.OrdinalIgnoreCase);

            return (!hasError, combined);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            if (File.Exists(tempScriptPath))
            {
                try { File.Delete(tempScriptPath); } catch { }
            }
        }
    }

    private static async Task<(bool success, string output)> RunPowerShellAsync(string script, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            string stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            string stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            bool success = proc.ExitCode == 0 && string.IsNullOrWhiteSpace(stderr);
            return (success, success ? stdout : $"{stdout}\n{stderr}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string CleanOutput(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Операция выполнена успешно.";
        var lines = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                       .Where(l => !l.Contains("Microsoft DiskPart") && 
                                   !l.Contains("Корпорация Майкрософт") && 
                                   !l.Contains("Copyright") &&
                                   !l.Contains("Сведения о компьютере:") &&
                                   !l.Contains("На компьютере:"));
        return string.Join("\n", lines).Trim();
    }

    private static void LogAction(string op, string target, StorageRiskLevel risk, string status, string details)
    {
        OperationLogs.Insert(0, new StorageOperationLog
        {
            OperationName = op,
            TargetDescription = target,
            RiskLevel = risk,
            Status = status,
            Details = details.Trim()
        });
    }
}
