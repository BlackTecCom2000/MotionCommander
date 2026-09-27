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
            sb.AppendLine($"  - Серийный номер:      {(string.IsNullOrWhiteSpace(d.SerialNumber) ? "не сообщается контроллером" : d.SerialNumber)}");
            sb.AppendLine($"  - Полный объем:        {d.TotalSizeFormatted}");
            sb.AppendLine($"  - Свободно места:      {d.FreeSpaceFormatted} ({d.FreeSpacePercent:F1}%)");
            sb.AppendLine($"  - Стиль разметки:      {d.PartitionStyle}");
            // ВАЖНО: в отчёте явно разделены измеренные и недоступные данные.
            // Раньше здесь печатались подставленные константы (38 °C, 8% износа,
            // «Идеально выровнен»), которые выглядели как результат диагностики.
            string scoreText = d.Score.IsCalculated
                ? $"{d.Score.TotalScore:F0}/100 (Grade: {d.Score.Grade})"
                : "нет данных для оценки";

            string healthText = string.IsNullOrWhiteSpace(d.HealthStatus)
                ? "не измерено"
                : $"{d.HealthStatus} (измерено через WMI)";

            string pohText = d.HasPowerOnHours
                ? $"{d.PowerOnHours:N0} часов"
                : "нет данных";

            string cyclesText = d.HasPowerCycles
                ? $"{d.PowerCycles:N0} включений"
                : "нет данных";

            string errorsText = d.HasErrorCounts
                ? $"{d.ReadErrorsTotal:N0} / {d.WriteErrorsTotal:N0}"
                : "нет данных";

            string trimText = !d.HasTrimInfo ? "нет данных"
                : d.IsTrimEnabled ? "поддерживается и включён"
                : "поддерживается, но ОТКЛЮЧЁН";

            string alignText = !d.HasSectorAlignment ? "не проверялось"
                : d.Is4KAligned ? "подтверждено"
                : "секторы не выровнены";

            sb.AppendLine($"  - Состояние здоровья:  {healthText}");
            sb.AppendLine($"  - Итоговая оценка:     {scoreText}");
            sb.AppendLine($"  - Температура:         {d.TemperatureFormatted} ({d.TemperatureStatus})");
            sb.AppendLine($"  - Ресурс (Wear Level): {(d.HasWear ? $"износ {d.WearLevelPercent:F0}%, остаток {d.LifetimeRemainingPercent:F0}%" : "нет данных")}");
            sb.AppendLine($"  - Время наработки:     {pohText} (Циклов пуска: {cyclesText})");
            sb.AppendLine($"  - Ошибки чтения/записи:{errorsText}");
            sb.AppendLine($"  - Фрагментация:        {d.FragmentationFormatted}");
            sb.AppendLine($"  - TRIM:                {trimText}");
            sb.AppendLine($"  - Выравнивание 4K:     {alignText}");
            sb.AppendLine($"  - Источник данных:     {SourceName(d.Source)}");
            sb.AppendLine();

            if (!d.HasRealTelemetry && d.TelemetryNote.Length > 0)
            {
                sb.AppendLine($"  ПРИМЕЧАНИЕ: {d.TelemetryNote}");
                sb.AppendLine("  Показатели не подставлялись: приложение показывает «нет данных» вместо");
                sb.AppendLine("  предположительных значений.");
                sb.AppendLine();
            }

            sb.AppendLine("  СТРУКТУРА РАЗДЕЛОВ И ТОМОВ:");
            foreach (var p in d.Partitions)
            {
                string letter = string.IsNullOrEmpty(p.DriveLetter) ? "<Без буквы>" : $"{p.DriveLetter}:";
                sb.AppendLine($"    • Раздел #{p.PartitionNumber}: {letter,-10} {p.SizeFormatted,-10} FS: {p.FileSystem,-8} {p.DisplayName} (Занято: {p.UsedPercent:F0}%)");
            }
            sb.AppendLine();

            if (d.HasSmartAttributes && d.SmartAttributes.Count > 0)
            {
                sb.AppendLine("  АТРИБУТЫ S.M.A.R.T. (реально прочитаны с контроллера):");
                sb.AppendLine("    ID   Имя атрибута                                Значение       Статус");
                sb.AppendLine("    ----------------------------------------------------------------------");
                foreach (var attr in d.SmartAttributes)
                {
                    sb.AppendLine($"    0x{attr.Id:X2} {attr.Name,-42} {attr.RawValueFormatted,-14} [{attr.Status}]");
                }
                sb.AppendLine();
            }
            else
            {
                // Раньше блок S.M.A.R.T. печатался ВСЕГДА, с выдуманными
                // значениями Current=100 и Status="Good".
                sb.AppendLine("  АТРИБУТЫ S.M.A.R.T.: прочитать не удалось — блок не приводится,");
                sb.AppendLine("  чтобы не выдавать предположительные значения за результат диагностики.");
                sb.AppendLine();
            }

            sb.AppendLine("--------------------------------------------------------------------------------");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>Человекочитаемое имя источника данных.</summary>
    private static string SourceName(TelemetrySource source) => source switch
    {
        TelemetrySource.Smart => "таблица S.M.A.R.T. (root\\wmi)",
        TelemetrySource.Wmi => "счётчики надёжности MSFT (WMI)",
        TelemetrySource.Utility => "системные утилиты (fsutil/defrag)",
        _ => "данные недоступны"
    };

    public static string GenerateJsonReport(IEnumerable<StorageDisk> disks)
    {
        return JsonSerializer.Serialize(disks, new JsonSerializerOptions { WriteIndented = true });
    }

    public static string GenerateCsvReport(IEnumerable<StorageDisk> disks)
    {
        var sb = new StringBuilder();

        // Добавлены колонки HasTemperature/HasWear/… и DataSource.
        // Без них невозможно отличить реальное измерение от подставленного
        // значения: раньше колонка TemperatureC всегда содержала число.
        sb.AppendLine(
            "DiskNumber,Model,SerialNumber,BusType,MediaType,TotalSizeBytes,FreeSpaceBytes," +
            "HealthStatus,HealthStatusMeasured,TemperatureC,HasTemperature," +
            "WearPercent,HasWear,PowerOnHours,HasPowerOnHours,PowerCycles,HasPowerCycles," +
            "ReadErrorsTotal,WriteErrorsTotal,FragmentationPercent,HasFragmentation," +
            "TrimEnabled,HasTrimInfo,Score,ScoreCalculated,DataSource");

        foreach (var d in disks)
        {
            // Значение, которое не измерено, экспортируем пустым, а не нулём:
            // ноль — это тоже показание, и он был бы введён в заблуждение.
            string temp = d.HasTemperature ? d.TemperatureC.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) : "";
            string wear = d.HasWear ? d.WearLevelPercent.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) : "";
            string poh = d.HasPowerOnHours ? d.PowerOnHours.ToString() : "";
            string cyc = d.HasPowerCycles ? d.PowerCycles.ToString() : "";
            string frag = d.HasFragmentation ? d.FragmentationPercent.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) : "";
            string score = d.Score.IsCalculated ? d.Score.TotalScore.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) : "";

            sb.AppendLine(string.Join(',',
                d.DiskNumber,
                Csv(d.Model),
                Csv(d.SerialNumber),
                d.BusType,
                d.MediaType,
                d.TotalSizeBytes,
                d.TotalFreeBytes,
                Csv(d.HealthStatus),
                !string.IsNullOrWhiteSpace(d.HealthStatus) ? "yes" : "no",
                temp,
                d.HasTemperature ? "yes" : "no",
                wear,
                d.HasWear ? "yes" : "no",
                poh,
                d.HasPowerOnHours ? "yes" : "no",
                cyc,
                d.HasPowerCycles ? "yes" : "no",
                d.HasErrorCounts ? d.ReadErrorsTotal.ToString() : "",
                d.HasErrorCounts ? d.WriteErrorsTotal.ToString() : "",
                frag,
                d.HasFragmentation ? "yes" : "no",
                d.HasTrimInfo ? (d.IsTrimEnabled ? "enabled" : "disabled") : "",
                d.HasTrimInfo ? "yes" : "no",
                score,
                d.Score.IsCalculated ? "yes" : "no",
                Csv(SourceName(d.Source))));
        }

        return sb.ToString();
    }

    /// <summary>Экранирует значение для CSV и защищает от CSV-инъекции в Excel.</summary>
    private static string Csv(string? value)
    {
        string v = value ?? "";

        // Ведущие =, +, -, @ заставляют Excel трактовать ячейку как формулу.
        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@')
        {
            v = "'" + v;
        }

        return "\"" + v.Replace("\"", "\"\"") + "\"";
    }

    public static async Task SaveReportToFileAsync(string filePath, string content)
    {
        await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);
    }
}
