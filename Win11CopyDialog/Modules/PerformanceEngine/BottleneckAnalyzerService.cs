using System;
using System.Diagnostics;
using System.Linq;

namespace Win11CopyDialog.Modules.PerformanceEngine;

public enum UserActivityContext
{
    Auto,           // Автоматическое определение
    Gaming,         // 🎮 Игры и 3D-графика
    FileTransfer,   // 📁 Копирование и архивация файлов
    Benchmark,      // 🏎 Бенчмарк и стресс-тест
    GeneralWork     // ☕ Офис, веб и покой
}

public sealed class ContextBottleneckResult
{
    public UserActivityContext ActiveContext { get; set; } = UserActivityContext.GeneralWork;
    public string ContextDisplayName { get; set; } = "Покой / Офисная работа";
    public string StatusBadge { get; set; } = "⚡ Оптимально";
    public string StatusColor { get; set; } = "#10B981";
    public string Title { get; set; } = "Сбалансированная работа системы";
    public string Description { get; set; } = "Все компоненты (CPU, память, шина I/O и накопители) работают синхронно без узких мест.";
    public string ActionableRecommendation { get; set; } = "Система работает в штатном режиме, дополнительных вмешательств не требуется.";
    public string DataBasis { get; set; } = "";
    public bool IsTelemetrySufficient { get; set; } = true;
    public string MissingTelemetryWarning { get; set; } = "";
}

public static class BottleneckAnalyzerService
{
    public static ContextBottleneckResult Analyze(UserActivityContext selectedContext, MonitorSample sample)
    {
        var result = new ContextBottleneckResult();

        // 1. Определение контекста (если выбран Auto)
        var effectiveContext = selectedContext;
        if (effectiveContext == UserActivityContext.Auto)
        {
            effectiveContext = DetectCurrentContext(sample);
        }

        result.ActiveContext = effectiveContext;
        result.ContextDisplayName = effectiveContext switch
        {
            UserActivityContext.Gaming => "🎮 Игры и 3D-графика",
            UserActivityContext.FileTransfer => "📁 Копирование файлов / I/O",
            UserActivityContext.Benchmark => "🏎 Бенчмарк / Стресс-тест",
            _ => "☕ Офис, веб и покой"
        };

        // 2. Сбор фактов телеметрии
        string diskSpeedText = $"{sample.DiskTotalMBps:F1} МБ/с (чтение: {sample.DiskReadMBps:F1}, запись: {sample.DiskWriteMBps:F1})";
        string tempText = sample.HasDiskTemp ? $"{sample.DiskTempC:F0} °C" : "н/д";

        result.DataBasis = $"Фактические метрики: CPU={sample.CpuPercent:F0}%, RAM={sample.RamPercent:F0}% ({sample.RamUsedGb:F1}/{sample.RamTotalGb:F1} ГБ), " +
                           $"Диск: активность={sample.DiskActivePercent:F0}%, очередь={sample.DiskQueueDepth:F1}, задержка={sample.DiskLatencyMs:F1} мс, скорость={diskSpeedText}, темп={tempText}. " +
                           $"Контекст: {result.ContextDisplayName}.";

        // Проверка достаточности информации
        if (!sample.HasDiskTemp)
        {
            result.IsTelemetrySufficient = false;
            result.MissingTelemetryWarning = "⚠ Внимание: Датчик температуры накопителя не отдал данные (для полного анализа NVMe рекомендуется запуск с правами Администратора).";
        }

        // 3. Анализ узких мест в зависимости от контекста
        switch (effectiveContext)
        {
            case UserActivityContext.Gaming:
                // В играх критична задержка диска (stuttering) и загрузка CPU
                if (sample.DiskLatencyMs > 25.0 || (sample.DiskActivePercent > 80.0 && sample.DiskQueueDepth > 8.0))
                {
                    result.StatusBadge = "🚨 Узкое место: Диск";
                    result.StatusColor = "#EF4444";
                    result.Title = "Задержка дисковой подсистемы (Микрофризы в игре)";
                    result.Description = $"Задержка отклика накопителя составляет {sample.DiskLatencyMs:F1} мс при очереди {sample.DiskQueueDepth:F1}. При подгрузке текстур и локаций это вызывает статтеры кадров.";
                    result.ActionableRecommendation = "Что сделать: закройте фоновые торренты и обновления Windows; для игр используйте NVMe/SSD, а не HDD; включите профиль «Игры».";
                    return result;
                }
                if (sample.CpuPercent > 88.0)
                {
                    result.StatusBadge = "⚠️ Узкое место: CPU";
                    result.StatusColor = "#F59E0B";
                    result.Title = "Предел вычислительной мощности процессора";
                    result.Description = $"Процессор загружен на {sample.CpuPercent:F0}%. Фоновые задачи конкурируют с игровым движком за потоки CPU.";
                    result.ActionableRecommendation = "Что сделать: проверьте вкладку «Фоновые процессы» и завершите фоновые клиенты, потребляющие ресурсы CPU.";
                    return result;
                }
                if (sample.RamPercent > 90.0)
                {
                    result.StatusBadge = "⚠️ Узкое место: RAM";
                    result.StatusColor = "#F59E0B";
                    result.Title = "Дефицит оперативной памяти";
                    result.Description = $"Занято {sample.RamPercent:F0}% памяти ({sample.RamUsedGb:F1} ГБ). Система начинает сбрасывать данные в файл подкачки на диск.";
                    result.ActionableRecommendation = "Что сделать: закройте браузеры с большим числом вкладок перед запуском игры для освобождения 2-4 ГБ RAM.";
                    return result;
                }
                break;

            case UserActivityContext.FileTransfer:
                // При копировании узкое место — пропускная способность накопителя или одного физического диска
                if (sample.DiskActivePercent > 92.0 && sample.DiskTotalMBps < 80.0)
                {
                    result.StatusBadge = "⚠️ Узкое место: Скорость I/O";
                    result.StatusColor = "#F59E0B";
                    result.Title = "Предел записи накопителя / исчерпание SLC-кэша";
                    result.Description = $"Диск загружен на 100%, но скорость составляет всего {sample.DiskTotalMBps:F1} МБ/с. На SSD исчерпан динамический SLC-кэш, либо задействован медленный порт USB 2.0 / SATA.";
                    result.ActionableRecommendation = "Что сделать: освободите не менее 15% места на SSD для восстановления SLC-кэша; при копировании больших файлов используйте Direct I/O буферизацию.";
                    return result;
                }
                if (sample.DiskQueueDepth > 15.0)
                {
                    result.StatusBadge = "⚠️ Высокая очередь I/O";
                    result.StatusColor = "#F59E0B";
                    result.Title = "Перегрузка очереди запросов ввода/вывода";
                    result.Description = $"Очередь запросов накопителя достигла {sample.DiskQueueDepth:F1}. Контроллер не успевает обрабатывать одновременные потоки.";
                    result.ActionableRecommendation = "Что сделать: в Motion Commander используется адаптивная буферизация — дождитесь выравнивания потока.";
                    return result;
                }
                break;

            case UserActivityContext.Benchmark:
                if (sample.HasDiskTemp && sample.DiskTempC >= 70.0)
                {
                    result.StatusBadge = "🚨 Термотроттлинг";
                    result.StatusColor = "#EF4444";
                    result.Title = "Критический перегрев накопителя при стресс-тесте";
                    result.Description = $"Температура ядра накопителя достигла {sample.DiskTempC:F0} °C. Контроллер принудительно снижает тактовую частоту.";
                    result.ActionableRecommendation = "Что сделать: дайте накопителю остыть; проверьте прижим радиатора M.2 и циркуляцию воздуха в зоне слота PCIe.";
                    return result;
                }
                break;

            case UserActivityContext.GeneralWork:
            default:
                // В покое / офисе высокая загрузка — признак аномальной активности
                if (sample.CpuPercent > 45.0)
                {
                    result.StatusBadge = "⚠️ Аномальная нагрузка CPU";
                    result.StatusColor = "#F59E0B";
                    result.Title = "Высокая фоновая нагрузка в режиме простоя";
                    result.Description = $"В режиме покоя процессор загружен на {sample.CpuPercent:F0}%. Работают тяжелые фоновые службы Windows или сторонние утилиты.";
                    result.ActionableRecommendation = "Что сделать: проверьте раздел «Контроль фоновой нагрузки» ниже для выявления скрытых процессов.";
                    return result;
                }
                if (sample.DiskActivePercent > 40.0)
                {
                    result.StatusBadge = "⚠️ Фоновая активность диска";
                    result.StatusColor = "#F59E0B";
                    result.Title = "Постоянное обращение к диску в режиме простоя";
                    result.Description = $"Активность диска составляет {sample.DiskActivePercent:F0}% без активных действий пользователя (возможно, индексация Windows Search или антивирус).";
                    result.ActionableRecommendation = "Что сделать: проверьте службу индексации поиска Windows и состояние автозагрузки приложений.";
                    return result;
                }
                break;
        }

        // По умолчанию: все показатели в норме
        result.StatusBadge = "⚡ Оптимальный баланс";
        result.StatusColor = "#10B981";
        result.Title = "Все аппаратные ресурсы сбалансированы";
        result.Description = $"Загрузка CPU ({sample.CpuPercent:F0}%), памяти ({sample.RamPercent:F0}%) и дисковой шины ({sample.DiskActivePercent:F0}%) находится в оптимальных границах без задержек.";
        result.ActionableRecommendation = "Рекомендация: аппаратная конфигурация работает с максимальной отдачей. Изменений не требуется.";
        return result;
    }

    private static UserActivityContext DetectCurrentContext(MonitorSample sample)
    {
        // 1. Проверяем наличие активных копирований в Motion Commander
        if (sample.DiskTotalMBps > 80.0 || sample.DiskActivePercent > 70.0)
        {
            return UserActivityContext.FileTransfer;
        }

        // 2. Проверяем процессы (игры, лаунчеры, бенчмарки)
        try
        {
            var processes = Process.GetProcesses();
            foreach (var p in processes)
            {
                string name = p.ProcessName.ToLowerInvariant();
                if (name.Contains("steam") || name.Contains("epicgames") || name.Contains("game") ||
                    name.Contains("cyberpunk") || name.Contains("dota") || name.Contains("cs2") ||
                    name.Contains("unreal") || name.Contains("unity"))
                {
                    return UserActivityContext.Gaming;
                }
                if (name.Contains("crystaldisk") || name.Contains("cinebench") || name.Contains("furmark") || name.Contains("3dmark"))
                {
                    return UserActivityContext.Benchmark;
                }
            }
        }
        catch { }

        return UserActivityContext.GeneralWork;
    }
}
