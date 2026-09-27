using System.Windows;
using System.Windows.Controls;

namespace Win11CopyDialog.Helpers;

/// <summary>
/// Безопасная установка значения ProgressBar.
///
/// <para><b>Зачем это нужно.</b> WPF бросает <c>ArgumentOutOfRangeException</c>,
/// если присвоить <c>ProgressBar.Value</c> значение вне диапазона
/// [Minimum, Maximum]. Многие источники прогресса вычисляют процент как
/// <c>обработано / всего * 100</c> без верхней клампа, и значение легко
/// превышает 100 (например, если файл растёт во время хеширования, либо
/// если «общий» размер в отчёте системного процесса занижен).</para>
///
/// <para>Такой бросок происходит внутри колбэка <c>Progress&lt;T&gt;</c> или
/// лямбды <c>Dispatcher.Invoke</c>, то есть на UI-потоке вне пользовательского
/// try/catch, и приводит к фатальному крашу приложения.</para>
/// </summary>
public static class ProgressGuard
{
    /// <summary>
    /// Присваивает значение прогресс-бару, зажатым в допустимый диапазон.
    /// Значения NaN и бесконечности также обрабатываются.
    /// </summary>
    public static void SetSafe(this ProgressBar bar, double value)
    {
        if (bar is null) return;

        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            value = 0;
        }

        double min = bar.Minimum;
        double max = bar.Maximum;

        // Защита от некорректного Maximum, заданного в XAML.
        if (max < min) max = min;

        bar.Value = Math.Clamp(value, min, max);
    }

    /// <summary>
    /// Присваивает значение кастомному <c>FluidProgressBar</c> (наследник
    /// FrameworkElement, а не ProgressBar) с тем же зажатием в [0, 100].
    /// </summary>
    public static void SetSafe(this Controls.FluidProgressBar bar, double value)
    {
        if (bar is null) return;

        if (double.IsNaN(value) || double.IsInfinity(value)) value = 0;

        bar.Value = Math.Clamp(value, 0, 100);
    }

    /// <summary>Нормализует «сырой» процент 0..100 в диапазон прогресс-бара.</summary>
    public static double Normalize(double percent) => Math.Clamp(percent, 0, 100);
}

/// <summary>
/// Безопасное закрытие диалогового окна.
///
/// <para>WPF бросает <c>InvalidOperationException</c> при присвоении
/// <c>DialogResult</c> на НЕмодальном окне (созданном через <c>Show()</c>,
/// а не <c>ShowDialog()</c>).</para>
///
/// <para>В проекте эти окна открываются обоими способами: <c>MainWindow</c>
/// вызывает <c>ShowDialog()</c>, а демонстрационные пути в
/// <c>App.xaml.cs</c> — <c>Show()</c>. Раньше присваивание
/// <c>DialogResult = true</c> бросалось во втором случае: у пользователя
/// появлялось ложное сообщение «Ошибка создания архива» уже ПОСЛЕ успешной
/// записи файла, а <c>Close()</c> не достигался и окно зависало.</para>
/// </summary>
public static class DialogCloser
{
    /// <summary>
    /// Закрывает окно, устанавливая DialogResult только если оно модальное.
    /// </summary>
    public static void CloseWithResult(this Window window, bool result)
    {
        if (window is null) return;

        try
        {
            // У WPF нет публичного свойства IsModal. Признак модальности —
            // непустой DialogResult (ShowDialog() устанавливает его в false
            // при закрытии), а также проверка ниже через try/catch:
            // немодальное окно бросает InvalidOperationException.
            window.DialogResult = result;
        }
        catch (InvalidOperationException)
        {
            // Немодальное окно (Show() вместо ShowDialog()) либо уже закрыто.
            // Диалог всё равно необходимо закрыть.
        }

        try
        {
            window.Close();
        }
        catch (InvalidOperationException)
        {
            // Окно уже в процессе закрытия.
        }
    }

    /// <summary>Закрывает окно без установки результата.</summary>
    public static void CloseSafely(this Window window) => window.CloseWithResult(true);
}
