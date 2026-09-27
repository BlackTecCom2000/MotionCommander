using System.Windows;
using System.Windows.Threading;

namespace Win11CopyDialog.Helpers;

/// <summary>
/// Безопасный маршалинг обновлений UI.
///
/// <para><b>Почему нельзя Dispatcher.Invoke из рабочего потока.</b>
/// <c>Dispatcher.Invoke</c> — синхронный блокирующий вызов: вызывающий поток
/// останавливается до тех пор, пока UI-поток не выполнит делегат. Потоки
/// конвейера копирования (<c>CopyEngine</c> / <c>StreamingPipeline</c>) физически
/// пишут байты в файл и вызывают телеметрию каждые ~100 мс. Блокировка этих
/// потоков на UI означает, что любая задержка отрисовки (модальный MessageBox,
/// ShowDialog, тяжёлый layout) напрямую снижает скорость копирования и создаёт
/// потенциальный дедлок.</para>
///
/// <para><b>Что делает этот класс.</b> <see cref="BeginInvoke"/> — неблокирующая
/// отправка в очередь диспетчера. Дополнительно выполняется проверка
/// <see cref="Dispatcher.HasShutdownStarted"/>, чтобы не бросать
/// <c>TaskCanceledException</c> на рабочем потоке, когда приложение закрывается.</para>
/// </summary>
public static class UiDispatcher
{
    /// <summary>
    /// Ставит действие в очередь UI-потока без блокировки вызывающего потока.
    /// Если приложение закрывается — действие молча отбрасывается.
    /// </summary>
    public static void BeginInvoke(this Dispatcher dispatcher, Action action)
    {
        if (dispatcher is null || action is null) return;

        // Диспетчер уже в процессе завершения — отправка вызовет TaskCanceledException
        // на фоновом потоке, что для fire-and-forget является необработанным исключением.
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;

        try
        {
            // BeginInvoke не ждёт выполнения. Приоритет ApplicationIdle даёт UI
            // возможность сначала отрисовать пользовательский ввод, а телеметрия
            // копирования не должна его блокировать.
            _ = dispatcher.BeginInvoke(action, DispatcherPriority.Background);
        }
        catch (TaskCanceledException)
        {
            // Гонка с закрытием приложения — безвредно.
        }
        catch (InvalidOperationException)
        {
            // Диспетчер уже остановлен.
        }
    }

    /// <summary>Синхронное выполнение, но только если уже находимся на UI-потоке.</summary>
    public static void InvokeIfOnUiThread(this Dispatcher dispatcher, Action action)
    {
        if (dispatcher is null || action is null) return;

        if (dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(action);
    }
}
