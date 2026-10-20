using System;
using System.IO;
using System.Text;
using Win11CopyDialog.Helpers;

namespace Win11CopyDialog.Modules.UpdateEngine;

/// <summary>
/// Журнал конвейера обновления: проверка версии, загрузка с зеркал,
/// сверка SHA-256, подготовка и применение, откат.
/// </summary>
/// <remarks>
/// <para>Раньше сбой обновления оставлял след только в исчезающем
/// MessageBox. По нему невозможно понять, что именно случилось через
/// час или на другом компьютере: был ли недоступен манифест, сколько
/// попыток загрузки провалилось, совпала ли контрольная сумма.</para>
///
/// <para>Файл лежит в каталоге данных приложения (не рядом с программой:
/// запись в Program Files требует прав). Запись никогда не бросает
/// исключение наружу: журнал не должен ломать само обновление.</para>
/// </remarks>
public static class UpdateLog
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Sync = new();

    public static string LogFilePath => Path.Combine(AppPaths.LogsDirectory, "update.log");

    public static void Info(string stage, string message) => Write("INFO", stage, message);
    public static void Warn(string stage, string message) => Write("WARN", stage, message);
    public static void Error(string stage, string message) => Write("ERROR", stage, message);

    private static void Write(string level, string stage, string message)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDirectory);
            lock (Sync)
            {
                RotateIfNeeded();
                File.AppendAllText(
                    LogFilePath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] [{stage}] {message}{Environment.NewLine}",
                    new UTF8Encoding(false));
            }
        }
        catch
        {
            // Журнал недоступен — обновление всё равно должно работать.
        }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(LogFilePath);
            if (!info.Exists || info.Length < MaxBytes) return;

            string previous = LogFilePath + ".1";
            if (File.Exists(previous)) File.Delete(previous);
            File.Move(LogFilePath, previous);
        }
        catch
        {
            // Ротация — обслуживание, а не условие обновления.
        }
    }
}
