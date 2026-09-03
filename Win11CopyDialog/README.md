# Win11 Copy Dialog — универсальное окно копирования для Windows 11

Стилизованный диалог копирования файлов в духе Windows 11 (Fluent Design):
5 тем с настоящим системным блюром Mica / Acrylic, акцентные цвета (включая
системный), расширенный функционал — скорость, ETA, пауза/отмена/пропуск,
список файлов, график скорости, реальное копирование, прогресс в таскбаре.

## Запуск

Требуется .NET 8 SDK (Windows):

```powershell
cd "F:\ANTIGRAVITY\WIN11 COPY\Win11CopyDialog"
& "C:\Program Files\dotnet\dotnet.exe" build -c Release
& "C:\Program Files\dotnet\dotnet.exe" run -c Release
```

или откройте `Win11CopyDialog.csproj` в Visual Studio и нажмите F5.

## Структура

| Файл | Назначение |
|---|---|
| `MainWindow.*` | Конструктор-демо: темы, акценты, сценарии, скорость, опции |
| `CopyDialogWindow.*` | **Универсальное окно копирования** — переиспользуемый компонент |
| `Models/CopyEngine.cs` | Движок: симуляция + реальное копирование (пауза/отмена/пропуск) |
| `Models/CopyItem.cs` | Один файл операции |
| `Models/ThemeManager.cs` | 5 тем + 9 акцентов, живые обновления всех окон |
| `Controls/SpeedGraph.cs` | График скорости (кастомный рендер) |
| `Helpers/BackdropHelper.cs` | Mica / Acrylic / скругление углов через DWM |
| `Helpers/SystemAccent.cs` | Системный акцент + форматирование Б/КБ/МБ/ГБ |
| `App.xaml` | Общие стили Win11 (кнопки, карточки, шапка окна) |

## Темы

- ☀ Светлая · ☾ Тёмная · ◈ Mica светлая · ◈ Mica тёмная · ⬣ Acrylic
- Mica / Acrylic используют настоящий `DwmSetWindowAttribute` (Windows 11),
  на Windows 10 автоматически откатываются к сплошному фону.
- Акцент: системный (читается из DWM/реестра) + 8 пресетов.

## Motion Copy Engine (новый премиум-интерфейс)

```powershell
Win11CopyDialog.exe --motion-demo
```

Hero-визуализация потока (диск → диск, частицы ∝ МБ/с, кольцо прогресса),
fluid-прогресс с бликом, waveform-график скорости, интерполированные цифры,
5 stat-карт, состояния preparing/copying/paused/error/completed, микроанимации
opened/closed/hover/press, тёмная + светлая темы. Детали — в `PROJECT_SNAPSHOT.md`.

## Классический диалог (`CopyDialogWindow`)

```csharp
// Симуляция
var dlg = new CopyDialogWindow();
dlg.StartSimulation(new[] { ("film.mkv", 4_000_000_000L) }, speedBytesPerSec: 150 * 1024 * 1024);
dlg.SetDetails(true);
dlg.Show();

// Реальное копирование каталога
var dlg2 = new CopyDialogWindow();
dlg2.Show();
await dlg2.StartRealCopyAsync(pairs); // (source, dest)[)

// Тот же API у Motion Copy Engine
var motion = new MotionCopyWindow();
motion.StartSimulation(new[] { ("film.mkv", 4_000_000_000L) });
motion.Show();
// или: MotionCopyWindow.ShowSimulation(files, speed);
```

Настройки окна: `AutoCloseOnComplete`, `PlaySoundOnComplete`, `Topmost`.

## Функционал диалога

- Общий + пофайловый прогресс, скорость (сглаженная, с реалистичными просадками), ETA, счётчики
- Пауза / продолжение / отмена (с подтверждением) / пропуск файла
- Двойной клик по файлу в списке — пропустить его
- График скорости за последние ~90 замеров
- Прогресс и состояние (пауза/ошибка) в иконке таскбара
- Звук по завершении, автозакрытие (опционально)
