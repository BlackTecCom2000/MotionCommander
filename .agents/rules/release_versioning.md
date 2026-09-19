# Mandatory Version Bump on Every Update

Каждый раз при подготовке обновления или релиза Motion Commander:
1. **Всегда увеличивать версию программы на 1 больше** (patch-версия, e.g., 3.8.2 -> 3.8.3 -> 3.8.4).
2. Синхронизировать номер версии во всех конфигурациях:
   - `Win11CopyDialog/Win11CopyDialog.csproj`
   - `src/MotionCommander.Core/MotionCommander.Core.csproj`
   - `src/MotionCommander.Cli/MotionCommander.Cli.csproj`
   - `installer/MotionCommander.iss`
   - `version.json`
3. Выпускать и компилировать оба дистрибутива:
   - `MotionCommander-v{Version}-Setup.exe` (инсталлятор Windows)
   - `MotionCommander-v{Version}-Portable.zip` (портативная версия)
4. Создавать коммит, тег `v{Version}` и отправлять в Git `origin main --tags`.
