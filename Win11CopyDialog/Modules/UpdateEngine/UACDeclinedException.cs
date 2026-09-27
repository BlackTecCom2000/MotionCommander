using System;
using System.IO;

namespace Win11CopyDialog.Modules.UpdateEngine;

/// <summary>
/// Пользователь отказал в повышении прав при применении обновления.
///
/// <para><b>Зачем отдельный тип.</b> Отказ в UAC приходит из
/// <c>Process.Start</c> как <c>Win32Exception</c> с кодом 1223. Если
/// показывать его как обычную ошибку, пользователь видит «Отказано в
/// доступе» и решает, что обновление сломано. На самом деле он просто
/// не дал права администратора, и достаточно объяснить, что делать
/// дальше.</para>
///
/// <para>Отдельный тип также позволяет НЕ закрывать программу: окно с
/// несохранённой работой должно остаться открытым, если обновление не
/// начнётся.</para>
/// </summary>
public sealed class UACDeclinedException : IOException
{
    public UACDeclinedException(string message, Exception? inner = null)
        : base(message, inner) { }
}
