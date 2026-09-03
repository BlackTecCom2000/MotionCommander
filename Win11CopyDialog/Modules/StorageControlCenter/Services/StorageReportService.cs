using System.IO;
using System.Text;
using System.Text.Json;
using Win11CopyDialog.Modules.StorageControlCenter.Models;

namespace Win11CopyDialog.Modules.StorageControlCenter.Services;

public static class StorageReportService
{
    public static string GenerateTextReport(IEnumerable<StorageDisk> disks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("================================================================================");
        sb.AppendLine("                 MOTION COMMANDER — STORAGE CONTROL CENTER REPORT               ");
        sb.AppendLine($"                     Дата отчета: {DateTime.Now:dd.MM.yyyy HH:mm:ss}                     ");
        sb.AppendLine("================================================================================");
        sb.AppendLine();

        foreach (var d in disks)
        {
            sb.AppendLine($"[НАКОПИТЕЛЬ #{d.DiskNumber}: {d.Model}]");
            sb.AppendLine($"  - Тип устройства:      {d.MediaTypeString} ({d.BusTypeString})");
            sb.AppendLine($"  - Серийный номер:      {d.SerialNumber}");
            sb.AppendLine($"  - Полный объем:        {d.TotalSizeFormatted}");
            sb.AppendLine($"  - Свободно места:      {d.FreeSpaceFormatted} ({d.FreeSpacePercent:F1}%)");
            sb.AppendLine($"  - Стиль разметки:      {d.PartitionStyle}");
            sb.AppendLine($"  - Состояние здоровья:  {d.HealthStatus} (Оценка Score: {d.Score.TotalScore}/100 - Grade: {d.Score.Grade})");
            sb.AppendLine($"  - Температура ядра:    {d.TemperatureC:F0} °C ({d.TemperatureStatus})");
            sb.AppendLine($"  - Ресурс (Wear Level): Использовано {d.WearLevelPercent:F0}%, Оставшийся ресурс: {d.LifetimeRemainingPercent:F0}%");
            sb.AppendLine($"  - Время наработки:     {d.PowerOnHours} часов (Циклов пуска: {d.PowerCycles})");
            sb.AppendLine($"  - Поддержка TRIM:      {(d.IsTrimSupported ? "Да (Включен)" : "Не требуется / Нет")}");
            sb.AppendLine($"  - Выравнивание 4K:     {(d.Is4KAligned ? "Идеально выровнен (4K OK)" : "Смещение секторов")}");
            sb.AppendLine();

            sb.AppendLine("  СТРУКТУРА РАЗДЕЛОВ И ТОМОВ:");
            foreach (var p in d.Partitions)
            {
                string letter = string.IsNullOrEmpty(p.DriveLetter) ? "<Без буквы>" : $"{p.DriveLetter}:";
                sb.AppendLine($"    • Раздел #{p.PartitionNumber}: {letter,-10} {p.SizeFormatted,-10} FS: {p.FileSystem,-8} {p.DisplayName} (Занято: {p.UsedPercent:F0}%)");
            }
            sb.AppendLine();

            if (d.SmartAttributes.Count > 0)
            {
                sb.AppendLine("  АТРИБУТЫ S.M.A.R.T. И ДИАГНОСТИКА:");
                sb.AppendLine("    ID   Имя атрибута                                Значение       Статус");
                sb.AppendLine("    ----------------------------------------------------------------------");
                foreach (var attr in d.SmartAttributes)
                {
                    sb.AppendLine($"    0x{attr.Id:X2} {attr.Name,-42} {attr.RawValueFormatted,-14} [{attr.Status}]");
                }
                sb.AppendLine();
            }

            sb.AppendLine("--------------------------------------------------------------------------------");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static string GenerateJsonReport(IEnumerable<StorageDisk> disks)
    {
        return JsonSerializer.Serialize(disks, new JsonSerializerOptions { WriteIndented = true });
    }

    public static string GenerateCsvReport(IEnumerable<StorageDisk> disks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("DiskNumber,Model,BusType,MediaType,TotalSizeBytes,FreeSpaceBytes,HealthStatus,TemperatureC,Score");
        foreach (var d in disks)
        {
            sb.AppendLine($"{d.DiskNumber},\"{d.Model}\",{d.BusType},{d.MediaType},{d.TotalSizeBytes},{d.TotalFreeBytes},{d.HealthStatus},{d.TemperatureC},{d.Score.TotalScore}");
        }
        return sb.ToString();
    }

    public static async Task SaveReportToFileAsync(string filePath, string content)
    {
        await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);
    }
}
