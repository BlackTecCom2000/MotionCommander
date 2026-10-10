using System;
using System.Collections.Generic;
using System.IO;

namespace Win11CopyDialog.Models;

/// <summary>Что делать, если файл назначения уже существует.</summary>
public enum OverwritePolicy
{
    /// <summary>Создать уникальное имя. Ничего не теряется. Значение по умолчанию.</summary>
    AutoRename = 0,
    /// <summary>Перезаписать существующий файл.</summary>
    Overwrite = 1,
    /// <summary>Пропустить существующий файл.</summary>
    SkipExisting = 2,
    /// <summary>Перезаписать, только если исходный файл новее существующего.</summary>
    KeepNewer = 3
}

/// <summary>Результат развёртки источников в список пар файл→файл.</summary>
public sealed class ExpansionResult
{
    public List<(string sourceFile, string destFile)> Pairs { get; } = new();

    /// <summary>Источники, содержимое которых прочитать не удалось.</summary>
    public List<string> FailedSources { get; } = new();

    /// <summary>Причина сбоя перечисления. Пусто при успехе.</summary>
    public string EnumerationError { get; set; } = "";

    /// <summary>Сколько каталогов пропущено из-за точек повторного входа.</summary>
    public int SkippedReparsePoints { get; set; }

    public bool HasFailures => FailedSources.Count > 0 || EnumerationError.Length > 0;
}

public static class CopySourceExpander
{
    /// <summary>
    /// Разворачивает список источников (файлы и папки) в пары файл→файл.
    ///
    /// <para>Три критичных исправления относительно прежней реализации.</para>
    ///
    /// <para><b>1. Сбой перечисления больше не превращается в «успех».</b>
    /// Раньше Directory.GetFiles(src, "*", AllDirectories) бросал
    /// UnauthorizedAccessException, если НИ ОДИН подкаталог недоступен
    /// (перегрузка с SearchOption по умолчанию не игнорирует
    /// недоступные папки). Исключение глоталось catch { }, список оставался
    /// пустым, StartRealCopyAsync вызывал Finish(completed: true), а
    /// вызывающий код удалял исходную папку целиком. Один запрещённый
    /// подкаталог превращал «Переместить папку» в «Удалить папку».</para>
    ///
    /// <para><b>2. Точки повторного входа не обходятся рекурсивно.</b>
    /// Раньше SearchOption.AllDirectories шёл по junction/symlink без
    /// ограничения глубины. Цикл junction на предка давал бесконечную
    /// рекурсию и нехватку памяти. Теперь используется EnumerationOptions
    /// с пропуском ReparsePoint и собственным счётчиком глубины.</para>
    ///
    /// <para><b>3. Коллизии имён разрешаются заранее.</b>
    /// Два файла с одинаковым именем из разных папок раньше оба писались
    /// в один путь с FileMode.Create: первый уничтожался вторым, а при
    /// «перемещении» удалялись оба оригинала. Теперь для целевого пути,
    /// который уже занят в этой же операции, генерируется уникальное имя.
    /// Существующий на диске файл тоже никогда не затирается молча.</para>
    /// </summary>
    public static ExpansionResult Expand(
        IEnumerable<(string source, string dest)> inputs,
        OverwritePolicy policy = OverwritePolicy.AutoRename)
    {
        var result = new ExpansionResult();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (src, dst) in inputs)
        {
            if (string.IsNullOrWhiteSpace(src)) continue;

            if (Directory.Exists(src))
                ExpandDirectory(src, dst, result, policy, claimed);
            else if (File.Exists(src))
                ExpandSingleFile(src, dst, result, policy, claimed);
            else
                result.FailedSources.Add(src);
        }

        return result;
    }

    private static void ExpandDirectory(
        string src, string dst,
        ExpansionResult result, OverwritePolicy policy,
        HashSet<string> claimed)
    {
        string trimmed = src.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string folderName = Path.GetFileName(trimmed);
        if (string.IsNullOrEmpty(folderName)) folderName = "Folder";

        string targetBaseDir = dst;
        if (Directory.Exists(dst))
        {
            string dstName = Path.GetFileName(dst.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.Equals(dstName, folderName, StringComparison.OrdinalIgnoreCase))
                targetBaseDir = Path.Combine(dst, folderName);
        }

        try
        {
            Directory.CreateDirectory(targetBaseDir);
        }
        catch (Exception ex)
        {
            result.FailedSources.Add(src);
            AppendError(result, $"Не удалось создать папку назначения «{targetBaseDir}»: {ex.Message}");
            return;
        }

        // Обходим дерево САМИ, проверяя точки повторного входа на каждом
        // уровне, и СРАЗУ ведём относительный путь каждого файла.
        //
        // Относительный путь вычисляется НЕ через Path.GetRelativePath.
        // Тот работает со строковым сравнением префиксов, поэтому при
        // коротком имени каталога (формат 8.3, например
        // C:\Users\BLACKT~1\...) и длинном имени, которое возвращает
        // перечисление, он выдавал мусор вида «rc\alpha.bin».
        // Поскольку обход наш собственный, путь и так известен точно.
        //
        // Раньше здесь стоял Directory.GetFiles(..., RecurseSubdirectories =
        // true) ПЛЮС отдельный перебор подкаталогов. Так как рекурсия уже
        // была включена, каждый вложенный файл попадал в список дважды:
        // копировался дважды, а одноимённые файлы ещё и разводились
        // переименованием. Тест на 5 исходных файлов рапортовал о 8.
        var files = new List<(string full, string rel)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string dir, string rel)>();
        queue.Enqueue((SafeFullPath(src), ""));
        visited.Add(SafeFullPath(src));

        var oneLevel = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false
        };

        while (queue.Count > 0)
        {
            var (dir, dirRel) = queue.Dequeue();

            string[] hereFiles;
            try
            {
                hereFiles = Directory.GetFiles(dir, "*", oneLevel);
            }
            catch (Exception ex)
            {
                // Сбой перечисления обязан быть виден вызывающему: раньше
                // здесь стоял catch { }, и пустой список превращался в
                // «успешное» копирование с последующим удалением исходников.
                result.FailedSources.Add(src);
                AppendError(result, $"Не удалось прочитать папку «{dir}»: {ex.Message}");
                return;
            }

            foreach (var f in hereFiles)
            {
                string name = Path.GetFileName(f);
                files.Add((f, dirRel.Length == 0 ? name : dirRel + "\\" + name));
            }

            string[] subDirs;
            try
            {
                subDirs = Directory.GetDirectories(dir, "*", oneLevel);
            }
            catch (Exception ex)
            {
                AppendError(result, $"Не удалось прочитать подпапки «{dir}»: {ex.Message}");
                continue;
            }

            foreach (var sub in subDirs)
            {
                try
                {
                    var attrs = File.GetAttributes(sub);
                    // junction/symlink пропускаем: цикл на предке даёт
                    // бесконечную рекурсию, а junction наружу приводит к
                    // молчаливому копированию данных извне исходного дерева.
                    if ((attrs & FileAttributes.ReparsePoint) != 0)
                    {
                        result.SkippedReparsePoints++;
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    result.SkippedReparsePoints++;
                    AppendError(result, $"Каталог «{sub}» недоступен: {ex.Message}");
                    continue;
                }

                string full = SafeFullPath(sub);
                if (!visited.Add(full)) continue;

                string subName = Path.GetFileName(sub.TrimEnd('\\', '/'));
                queue.Enqueue((full, dirRel.Length == 0 ? subName : dirRel + "\\" + subName));
            }
        }

        foreach (var (full, rel) in files)
            AddPair(full, Path.Combine(targetBaseDir, rel), result, policy, claimed);
    }

    /// <summary>
    /// Относительный путь безопасно: при недоступности GetRelativePath
    /// возвращается к простому отбрасыванию общего префикса.
    /// </summary>
    private static string SafeRelativePath(string root, string full)
    {
        try
        {
            return Path.GetRelativePath(root, full);
        }
        catch
        {
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                string rest = full.Substring(root.Length).TrimStart('\\', '/');
                return rest;
            }
            return "";
        }
    }

    private static void ExpandSingleFile(
        string src, string dst,
        ExpansionResult result, OverwritePolicy policy,
        HashSet<string> claimed)
    {
        string targetFile = dst;
        if (Directory.Exists(dst))
        {
            targetFile = Path.Combine(dst, Path.GetFileName(src));
        }
        else
        {
            string? pDir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(pDir))
            {
                try { Directory.CreateDirectory(pDir); }
                catch (Exception ex)
                {
                    result.FailedSources.Add(src);
                    AppendError(result, $"Не удалось создать папку «{pDir}»: {ex.Message}");
                    return;
                }
            }
        }

        // Копирование файла в самого себя: иначе FileMode.Create уничтожил бы исходник.
        if (string.Equals(SafeFullPath(src), SafeFullPath(targetFile), StringComparison.OrdinalIgnoreCase))
            targetFile = GenerateDuplicateFileName(targetFile, claimed);

        AddPair(src, targetFile, result, policy, claimed);
    }

    /// <summary>
    /// Добавляет пару, разрешая конфликт имени.
    /// Не даёт двум файлам одной операции попасть в один путь
    /// и не даёт молча затереть уже существующий файл.
    /// </summary>
    private static void AddPair(
        string source, string desired,
        ExpansionResult result, OverwritePolicy policy,
        HashSet<string> claimed)
    {
        string target = desired;

        if (policy == OverwritePolicy.SkipExisting && File.Exists(target))
            return;

        if (policy == OverwritePolicy.KeepNewer && File.Exists(target))
        {
            try
            {
                var srcTime = File.GetLastWriteTimeUtc(source);
                var dstTime = File.GetLastWriteTimeUtc(target);
                if (dstTime >= srcTime)
                {
                    // Файл назначения новее или равен по времени: пропускаем копирование
                    return;
                }
            }
            catch { }
        }

        bool needsRename = false;
        if (policy == OverwritePolicy.AutoRename)
        {
            if (claimed.Contains(target)) needsRename = true;
            else if (File.Exists(target) &&
                     !string.Equals(SafeFullPath(source), SafeFullPath(target), StringComparison.OrdinalIgnoreCase))
            {
                needsRename = true;
            }
        }

        if (needsRename) target = GenerateDuplicateFileName(target, claimed);

        claimed.Add(target);
        result.Pairs.Add((source, target));
    }

    private static string GenerateDuplicateFileName(string filePath, HashSet<string>? claimed = null)
    {
        string dir = Path.GetDirectoryName(filePath) ?? "";
        string name = Path.GetFileNameWithoutExtension(filePath);
        string ext = Path.GetExtension(filePath);
        int counter = 1;
        while (true)
        {
            string candidate = Path.Combine(dir, $"{name} - Копия{(counter > 1 ? $" ({counter})" : "")}{ext}");
            if (!File.Exists(candidate) && (claimed is null || !claimed.Contains(candidate))) return candidate;
            counter++;
            if (counter > 9999) return Path.Combine(dir, $"{name} - Копия{Guid.NewGuid():N}{ext}");
        }
    }

    private static void AppendError(ExpansionResult result, string message)
    {
        if (result.EnumerationError.Length == 0) result.EnumerationError = message;
        else result.EnumerationError += " " + message;
    }

    private static string SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }
}
