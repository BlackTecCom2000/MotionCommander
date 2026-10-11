using System;

namespace Win11CopyDialog.Modules.UpdateEngine;

/// <summary>
/// Исключение, сигнализирующее о том, что патч-обновление несовместимо с установленной версией программы
/// (например, установлена устаревшая версия, изменились системные сборки или повреждён манифест патча),
/// и пользователю необходимо загрузить полный установочный пакет.
/// </summary>
public sealed class PatchIncompatibleException : Exception
{
    public string CurrentVersion { get; }
    public string TargetVersion { get; }
    public string MinCompatibleVersion { get; }

    public PatchIncompatibleException(
        string message,
        string currentVersion = "",
        string targetVersion = "",
        string minCompatibleVersion = "",
        Exception? inner = null)
        : base(message, inner)
    {
        CurrentVersion = currentVersion;
        TargetVersion = targetVersion;
        MinCompatibleVersion = minCompatibleVersion;
    }
}
