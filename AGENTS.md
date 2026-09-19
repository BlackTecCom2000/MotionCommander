# Motion Commander — Agent Guidelines & Rules

## 1. Release & Versioning Rule (MANDATORY)
**Каждый раз, когда делается обновление или релиз программы, ОБЯЗАТЕЛЬНО увеличивать версию на 1 больше (+1 к Patch версии)** (например: `3.8.2` → `3.8.3` → `3.8.4`):
- Обновить версию в `Win11CopyDialog/Win11CopyDialog.csproj` (`<Version>`, `<AssemblyVersion>`, `<FileVersion>`)
- Обновить версию в `src/MotionCommander.Core/MotionCommander.Core.csproj`
- Обновить версию в `src/MotionCommander.Cli/MotionCommander.Cli.csproj`
- Обновить `MyAppVersion` в `installer/MotionCommander.iss`
- Обновить `version` и URL-адреса в `version.json`
- Собрать и упаковать релизные артефакты:
  - `dist/MotionCommander-v{Version}-Setup.exe`
  - `dist/MotionCommander-v{Version}-Portable.zip`
  - Обновить `Latest` указатели в `dist/`
- Зафиксировать коммит в Git, проставить тег `v{Version}` и выполнить push в репозиторий GitHub (`main` и теги).

## 2. In-App Updater Compatibility
- В `version.json`:
  - `downloadUrl` и `installerUrl` должны отдавать совместимый архив обновления `MotionCommander-v{Version}-Portable.zip`, чтобы уже установленная у пользователя версия обновлялась бесшовно без ошибок распаковки.
  - `setupExeUrl` должен указывать на инсталлятор `MotionCommander-v{Version}-Setup.exe`.
