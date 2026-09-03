# Project Snapshot — Win11 Copy Dialog / Motion Copy Engine

Дата: 2026-09-03. Стек: .NET 8 + WPF (C#), без внешних зависимостей.
Сборка: `dotnet build -c Release`, 0 warnings / 0 errors. Самотест `--selftest` (3 окна, 5 с): exit 0.

## Архитектура

```
Win11CopyDialog/
├── App.xaml(.cs)              — ресурсы/стили, ThemeManager.Apply(), маршрутизация запуска
├── MainWindow.xaml(.cs)       — конструктор-демо: темы, акценты, сценарии, скорость, опции
├── CopyDialogWindow.xaml(.cs) — классический диалог копирования (Win11 Explorer-style)
├── MotionCopyWindow.xaml(.cs) — Motion Copy Engine (премиум-интерфейс, hero-визуализация)
├── Models/
│   ├── CopyEngine.cs          — движок: Simulation + RealCopy, пауза/отмена/пропуск, ETA, SpeedHistory
│   ├── CopyItem.cs            — файл операции (прогресс, статус, глиф)
│   └── ThemeManager.cs        — singleton: 5 тем + 9 акцентов, живое обновление всех окон
├── Controls/
│   ├── TransferVisualizer.cs  — hero: диски, поток частиц, кольцо прогресса, состояния
│   ├── FluidProgressBar.cs    — fluid-прогресс с бликом/свечением + indeterminate
│   ├── WaveformGraph.cs       — waveform скорости (Catmull-Rom→Bezier, светящаяся голова)
│   └── SpeedGraph.cs          — legacy-график классического диалога
└── Helpers/
    ├── Motion.cs              — motion-система: Damp/Lerp/Easing/Spring, длительности
    ├── BackdropHelper.cs      — DWM: Mica/Acrylic/скругление/тёмный режим (Win11, fallback Win10)
    └── SystemAccent.cs        — системный акцент (DWM→реестр→fallback) + форматтеры Б/КБ/МБ/ГБ
```

Поток данных: `CopyEngine` (10 Гц тики) → цели (`Progress`, `SpeedNorm`, `State`) →
`CompositionTarget.Rendering` (60+ FPS) интерполирует отображение через `Motion.Damp`.
Файловые операции и анимации изолированы: движок не знает о UI, контролы не блокируют копирование.

## UI-компоненты

| Компонент | Файл | Рендер |
|---|---|---|
| TransferVisualizer | Controls/TransferVisualizer.cs | DrawingContext, 1 цикл Rendering, пул ≤130 частиц + ≤70 burst, batched GeometryGroup |
| FluidProgressBar | Controls/FluidProgressBar.cs | Собственный Rendering-цикл, clip + градиенты, без эффектов blur |
| WaveformGraph | Controls/WaveformGraph.cs | Сглаживание Catmull-Rom→Bezier, заливка-градиент, пульс головы |
| MotionCopyWindow | MotionCopyWindow.xaml(.cs) | Hero + 5 stat-карт + waveform + список + плавающая панель |
| CopyDialogWindow | CopyDialogWindow.xaml(.cs) | Компакт/детали, TaskbarItemInfo, звук, автозакрытие |
| Окна | WindowChrome, CornerRadius 8, GlassFrame −1, кастомная шапка 40px |

## Motion System

- `Motion.Damp` (экспоненциальное сглаживание, FPS-независимое) — цифры %, МБ/с, энергия потока, fluid-заливка.
- Длительности: micro 150мс (файлы, закрытие), normal 200–240мс (hover, crossfade), large 320–450мс (появление окна), cinematic 700–900мс (pulse, галочка).
- Easing: CubicEase Out в XAML; EaseOutCubic/EaseInOutCubic + Spring в коде.
- Появление окна: opacity 0→1 + scale 0.965→1 (320мс). Закрытие: fade+scale 140мс через OnClosing.
- Кнопки: hover-glow, press-scale 0.96. Stat-карты: hover-scale 1.045 (150мс).
- Смена файла: fade out + slide −12 (120мс) → замена → fade in + slide (200мс).
- Кольцо прогресса и fluid-бар — непрерывная интерполяция, без скачков.

## Состояния (TransferState + pill + hint)

- Preparing: сканирующая полоса в hero, indeterminate fluid-бар, пилюля серая.
- Copying: поток частиц (скорость/плотность ∝ МБ/с), пилюля акцентная.
- Paused: энергия → 0 ускоренно (3.2/с), частицы замирают; скорость → 0; пилюля янтарная; таскбар Paused.
- Error: янтарное кольцо на узле-приёмнике (затухание 1.2с), пилюля красная, таскбар Error.
- Completed: pulse-кольцо (0.9с) + 70 burst-частиц + анимированная галочка (dash 700мс) + звук; пилюля зелёная.
- Cancelled: пилюля серая, таскбар Error; Cancel требует подтверждения.

## Модуль Storage Control Center (v2.0 NVMe/SSD/HDD Master Engine)

Интегрирован в главное окно под вкладкой **🖴 Накопители** (`--tab-storage`).

### Архитектура модуля:
```
Win11CopyDialog/Modules/StorageControlCenter/
├── Models/
│   ├── StorageDisk.cs             — физический накопитель (BusType, MediaType, температура, износ, SMART, партиции)
│   ├── StoragePartition.cs        — том/раздел (категории, пропорции карты, защищенные системные флаги)
│   ├── SmartAttribute.cs          — S.M.A.R.T. метрики, пороги, статусы, критичность
│   ├── StorageScore.cs            — алгоритм оценки здоровья диска 0–100 (A+, A, B, C, D)
│   ├── BenchmarkConfig.cs         — параметры CrystalDiskMark-подобного теста
│   ├── StorageRecommendation.cs   — AI Advisor советы и кнопки быстрых действий
│   ├── StorageCleanupItem.cs      — категории очистки кэша и тяжелые файлы (> 250MB)
│   └── StorageOperationLog.cs     — журнал безопасности с уровнями риска
├── Services/
│   ├── StorageDiscoveryService.cs — топология дисков (MSFT_Disk, MSFT_PhysicalDisk, CIM/WMI fallback)
│   ├── SmartHealthService.cs      — надежность и телеметрия NVMe / SATA S.M.A.R.T.
│   ├── StorageBenchmarkService.cs — неразрушающий бенчмарк (Seq 1M Q8T1, Random 4K Q32T1, IOPS, Latency)
│   ├── DiskOptimizerService.cs    — SSD TRIM (ReTrim) vs HDD Defrag (защита от износа SSD)
│   ├── PartitionManagementService.cs — создание, сжатие, расширение томов, блокировка системного C:
│   ├── FormatService.cs           — форматирование томов (NTFS, exFAT, FAT32) с аппаратной защитой C:
│   ├── DiskWipeService.cs         — безопасное затирание удаленных данных (Zero-Fill Free Space)
│   ├── StorageCleanupService.cs   — сканирование и очистка %TEMP%, Windows Temp, Crash Dumps
│   ├── StorageExplorerService.cs  — глубокий анализ тяжелых файлов (> 250 МБ)
│   ├── StorageAdvisorService.cs   — детектор узких мест, троттлинга и SLC-кэша
│   └── StorageReportService.cs    — экспорт отчетов (TXT, JSON, CSV)
└── Views/
    ├── StorageControlCenterView.xaml   — 6 подвкладок: Обзор & Health, Карта разделов, Бенчмарк, Оптимизация & TRIM, Анализ пространства, Безопасность & Wipe
    └── StorageControlCenterView.xaml.cs— логика переключения, интерактивная карта томов, асинхронные воркеры
```

## Режимы запуска

- без аргументов → MainWindow (файловый менеджер и архиватор);
- `--tab-storage` / `--storage` → запуск сразу на вкладке Storage Control Center;
- `--motion-demo` → сразу MotionCopyWindow с mixed-сценарием 128 файлов;
- `--selftest` → 3 окна + симуляции + пауза/продолжение, автовыход 5с, exit 0.

## Последние изменения (2026-09-04)

1. **Storage Control Center Master Engine**:
   - Реализована полная функциональность аппаратной и программной диагностики всех дисков (NVMe, SATA SSD, HDD, USB Flash).
   - Интерактивная карта разметки диска с пропорциональным масштабированием и цветовой кодировкой типов разделов (EFI, MSR, Basic Data, Recovery, Unallocated).
   - Неразрушающий бенчмарк линейной и случайной скорости чтения/записи, IOPS и времени отклика.
   - Раздельная оптимизация: нативный ReTrim для SSD/NVMe и глубокая дефрагментация секторов для HDD.
   - Анализатор и чистильщик временных данных Windows и поиск тяжелых файлов (> 250 МБ).
   - Защита системного загрузочного тома (C:) от случайного форматирования, сжатия и удаления.
   - Поддержка экспорта отчета в один клик.
2. **Плавная кинематическая прокрутка (Smooth Kinetic Scrolling)**:
   - Внедрен физический импульсный движок `SmoothScrollBehavior` со сглаживанием `EaseOutCubic`.
   - Добавлены ползунки кастомизации (множитель трения, дистанция шага колесика, время анимации).
   - Футуристический неоновый скроллбар с пульсирующим свечением и подсветкой бегунка при наведении.
