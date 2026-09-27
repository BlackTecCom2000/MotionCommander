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
/// <para><b>Почему метод называется Post, а не BeginInvoke.</b>
/// Раньше это было расширение с именем BeginInvoke. Но вызов
/// <c>dispatcher.BeginInvoke(action)</c> с аргументом типа Action
/// разрешается в МЕТОД ЭКЗЕМПЛЯРА
/// <c>Dispatcher.BeginInvoke(Delegate, params object[])</c>: Action неявно
/// приводится к Delegate, поэтому метод экземпляра подходит, а расширения
/// рассматриваются только когда подходящего метода экземпляра нет.
/// В результате расширение не вызывалось НИ РАЗУ: проверки
/// HasShutdownStarted не работали, а приоритет Background не применялся —
/// телеметрия копирования шла с обычным приоритетом, ahead пользовательского
/// ввода, то есть ровно наоборот задуманного.</para>
/// </summary>
public static class UiDispatcher
{
    /// <summary>
    /// Ставит действие в очередь UI-потока без блокировки вызывающего потока.
    /// Если приложение закрывается — действие молча отбрасывается.
    /// </summary>
    public static void Post(this Dispatcher dispatcher, Action action)
    {
        if (dispatcher is null || action is null) return;

        // Диспетчер уже в процессе завершения — отправка вызовет TaskCanceledException
        // на фоновом потоке, что для fire-and-forget является необработанным исключением.
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;

        try
        {
            // BeginInvoke не ждёт выполнения. Приоритет Background даёт UI
            // возможность сначала отрисовать пользовательский ввод, а телеметрия
            // копирования не должна его блокировать.
            _ = dispatcher.BeginInvoke(action, DispatcherPriority.Background);
        }
        catch (TaskCanceledException)
        {
            // Гонка с закрытием приложения — безвродно.
        }
        catch (InvalidOperationException)
        {
            // Диспетчер уже остановлен.
        }
    }

    /// <summary>Выполняет действие немедленно, если уже на UI-потоке, иначе ставит в очередь.</summary>
    public static void InvokeIfOnUiThread(this Dispatcher dispatcher, Action action)
    {
        if (dispatcher is null || action is null) return;

        if (dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Post(action);
    }
}
