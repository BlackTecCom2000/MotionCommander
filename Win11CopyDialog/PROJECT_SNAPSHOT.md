# Project Snapshot — Win11 Copy Dialog / Motion Commander

Дата: 2026-09-13. Стек: .NET 8 + WPF (C#), без внешних зависимостей.
Сборка: `dotnet build -c Release`, 0 warnings / 0 errors. Самотест `--selftest` (3 окна, 5 с): exit 0.

## Архитектура

```
Win11CopyDialog/
├── App.xaml(.cs)              — ресурсы/стили, ThemeManager.Apply(), маршрутизация запуска (--selftest, --filemanager, --motion-demo и др.)
├── MainWindow.xaml(.cs)       — конструктор-демо: темы, акценты, сценарии, скорость, опции, табы (Файлы/Архивы/Storage/Tools/Diagnostics)
├── MotionCopyWindow.xaml(.cs) — Motion Copy Engine (премиум-интерфейс): hero-визуализация, fluid-прогресс, waveform
├── CopyDialogWindow.xaml(.cs) — классический диалог копирования (Win11 Explorer-style)
├── FileManagerWindow.xaml(.cs) — полноценный файловый менеджер: дерево, список, навигация, копирование с motion-прогрессом
├── Models/
│   ├── CopyEngine.cs          — движок: Simulation + RealCopy, пауза/отмена/пропуск, ETA, SpeedHistory
│   ├── CopyItem.cs            — один файл операции
│   ├── FileEntry.cs           — файл/папка/диск: размер, иконка (Win32 SHGetFileInfo), метаданные, INotifyPropertyChanged
│   ├── ThemeManager.cs        — singleton: 5 тем + 9 акцентов, живое обновление всех окон
│   └── Motion.cs              — motion-система: Damp/Lerp/Easing/Spring, длительности
├── Controls/
│   ├── TransferVisualizer.cs  — hero: диски, поток частиц, кольцо прогресса, состояния (≤130 частиц)
│   ├── FluidProgressBar.cs    — fluid-прогресс с бликом/свечением + indeterminate
│   ├── WaveformGraph.cs       — waveform скорости (Catmull-Rom→Bezier, светящаяся голова)
│   ├── SpeedGraph.cs          — legacy-график классического диалога
│   ├── FileListControl.*      — ListView с колонками (Имя/Размер/Тип/Дата), сортировка, выбор, контекстное меню, горячие клавиши
│   └── FolderTreeControl.*    — TreeView дерево папок с раскрытием, loading-indicator, быстрый доступ
└── Helpers/
    ├── Motion.cs              — motion-система
    ├── BackdropHelper.cs      — DWM: Mica/Acrylic/скругление/тёмный режим
    └── SystemAccent.cs        — системный акцент + форматтеры Б/КБ/МБ/ГБ
└── PROJECT_SNAPSHOT.md        — этот файл
```

Поток данных: `CopyEngine` (10 Гц тики) → цели → `CompositionTarget.Rendering` (60+ FPS) интерполирует отображение через `Motion.Damp`.
Файловые операции и анимации изолированы: движок не знает о UI, контролы не блокируют копирование.

## UI-компоненты

| Компонент | Рендер |
|---|---|
| TransferVisualizer | DrawingContext, 1 цикл Rendering, пул ≤130 частиц + ≤70 burst, batched GeometryGroup |
| FluidProgressBar | Собственный Rendering-цикл, clip + градиенты, без эффектов blur |
| WaveformGraph | Сглаживание Catmull-Rom→Bezier, заливка-градиент, пульс головы |
| FileListControl | WPF ListView + GridView, VirtualizingPanel, стили триггеров |
| FolderTreeControl | WPF TreeView + HierarchicalDataTemplate, VirtualizingPanel |
| MotionCopyWindow | Hero + 5 stat-карт + waveform + список + плавающая панель, WindowChrome, Mica/Acrylic |
| FileManagerWindow | Tree + ListView + address bar + nav + motion-copy overlay, WindowChrome, Mica/Acrylic |

## Motion System

- `Motion.Damp` (экспоненциальное сглаживание, FPS-независимое) — цифры %, МБ/с, энергия потока, fluid-заливка.
- Длительности: micro 150мс, normal 200–240мс, large 320–450мс, cinematic 700–900мс.
- Easing: CubicEase Out в XAML; EaseOutCubic/EaseInOutCubic + Spring в коде.
- Появление окна: opacity 0→1 + scale 0.97→1 (280мс). Закрытие: fade+scale 140мс через OnClosing.
- Кнопки: hover-glow, press-scale 0.96–0.94. Stat-карты: hover-scale 1.045 (150мс).
- Смена файла (Motion): fade out + slide −12 (120мс) → замена → fade in + slide (200мс).
- Статусы (TransferState): Preparing (scan + indeterminate), Copying (particles), Paused (freeze), Error (glow), Completed (pulse + burst + галочка).

## Состояния

- **Preparing**: сканирующая полоса в hero, indeterminate fluid-бар, пилюля серая.
- **Copying**: поток частиц (скорость/плотность ∝ МБ/с), пилюля акцентная.
- **Paused**: энергия → 0 ускоренно, частицы замирают, скорость → 0, пилюля янтарная.
- **Error**: янтарное кольцо на узле-приёмнике, пилюля красная.
- **Completed**: pulse-кольцо + 70 burst-частиц + анимированная галочка (700мс) + звук.

## Режимы запуска (App.xaml.cs OnStartup)

| Аргумент | Действие |
|---|---|
| без аргументов | MainWindow (конструктор), стандартный запуск с UAC elevation |
| `--selftest` | 3 окна + симуляции + пауза/продолжение, автовыход 5с, exit 0 |
| `--motion-demo` | MotionCopyWindow с mixed-сценарием 128 файлов |
| `--filemanager` | FileManagerWindow — полноценный файловый менеджер |
| `--bench-cli [dir]` | Бенчмарк I/O в указанной папке |
| `--dark` / `--light` | Тема Mica Dark / Light |
| `--tab-transfer` / `--tab-storage` / `--tab-diagnostics` / `--tab-tools` | MainWindow с открытым табом |
| `--create-archive-demo` | CreateArchiveWindow |
| `--extract-archive-demo` | ExtractArchiveWindow |
| `--wiztree` | WizTreeAnalyzerWindow |
| `--duplicates` | DuplicateFinderWindow |
| `--drivers` | DriverInspectorWindow |
| `--settings-window` | SettingsWindow |
| `--folder-picker-demo` | CyberFolderPickerDialog |
| `--advanced-tools-demo` | AdvancedToolsWindow |
| `<путь>` | MainWindow с открытой папкой |

## Последние изменения (2026-09-13)

1. FileManagerWindow: полноценный файловый менеджер (Tree + ListView + Nav + AddressBar + Motion Copy)
2. FileEntry: модель файла/папки/диска с иконками Win32, размерами, INPC
3. FileService: навигация, обход дерева, реальное копирование с IProgress<CopyProgress>
4. FileListControl: колонки (Имя/Размер/Тип/Дата), сортировка, контекстное меню, горячие клавиши (C/X/V/F2/Del/Enter)
5. FolderTreeControl: TreeView с Expand/Collapse, loading indicator, быстрый доступ, создание папок
6. Интеграция: кнопка «Файловый менеджер» в Ribbon MainWindow, `--filemanager` arg в App
7. PROJECT_SNAPSHOT.md: полная документация архитектуры и компонентов

## Известные проблемы / ограничения

1. FileListControl: контекстное меню (Копировать/Вырезать/Вставить/Переименовать/Удалить) — UI есть, логика требует доработки (clipboard)
2. Drag-drop на FileManagerWindow: определена цель, но drag/drop handlers не реализованы
3. FileService.CopyAsync: реальное копирование работает, но не прерываемо корректно при паузе (Task.Delay с CancellationToken)
4. Иконки файлов: используются только системные иконки из SHGetFileInfo, кастомные иконки по расширению не поддерживаются
5. Mica/Acrylic: только Windows 11 (build ≥ 22000), на Win10 тихий fallback
6. Файловый менеджер: не поддерживает сетевые пути (UNC), FTP, архивы как папки (только физические файлы)

## Гит

Remote: `origin https://github.com/BlackTecCom2000/MotionCommander.git`
Текущий коммит: `8e26d41 feat: add FileManagerWindow and FileEntry/FileService components`
Предыдущий: `1d0998b Merge options into settings window and fix theme switching`
