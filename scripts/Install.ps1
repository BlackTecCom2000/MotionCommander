# Installer & Updater for Motion Commander
# Author: BlackTecCom - Jaborov Daler (MIT License)

$ErrorActionPreference = "Stop"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "   Motion Commander: Установка и обновление (BlackTecCom)   " -ForegroundColor Yellow
Write-Host "==========================================================" -ForegroundColor Cyan

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path "$repoRoot\Win11CopyDialog\Win11CopyDialog.csproj")) {
    $repoRoot = $PSScriptRoot
}

# 1. Читаем текущую версию приложения
$appVersion = "3.8.42"
$versionJsonPath = "$repoRoot\version.json"
if (Test-Path $versionJsonPath) {
    try {
        $vJson = Get-Content $versionJsonPath -Raw | ConvertFrom-Json
        if ($vJson.version) { $appVersion = $vJson.version }
    }
    catch { }
}

# 2. Функция поиска ранее установленной копии Motion Commander
function Find-ExistingInstallation {
    $candidates = @()

    # Проверка записей деинсталляции в реестре (HKLM и HKCU)
    $uninstallRoots = @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall"
    )

    foreach ($root in $uninstallRoots) {
        if (Test-Path $root) {
            Get-ChildItem -Path $root -ErrorAction SilentlyContinue | ForEach-Object {
                $prop = Get-ItemProperty -Path $_.PSPath -ErrorAction SilentlyContinue
                if ($prop) {
                    $name = $prop.DisplayName
                    $loc = $prop.InstallLocation
                    $isAppId = ($_.PSChildName -like "*{D37D5726-2F1E-4B07-B25C-2150E697DF2A}*")
                    $isNameMatch = ($name -and $name -like "*Motion Commander*")

                    if ($isAppId -or $isNameMatch) {
                        if ($loc -and (Test-Path $loc)) {
                            $exe = Join-Path $loc "Win11CopyDialog.exe"
                            if (Test-Path $exe) {
                                $candidates += [PSCustomObject]@{
                                    Path = (Get-Item $loc).FullName.TrimEnd('\')
                                    KeyPath = $_.PSPath
                                    DisplayName = $name
                                    Version = $prop.DisplayVersion
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    # Проверка стандартных каталогов на диске
    $knownDirs = @(
        "$env:ProgramFiles\Motion Commander",
        "${env:ProgramFiles(x86)}\Motion Commander",
        "$env:LOCALAPPDATA\Programs\Motion Commander",
        "$env:LOCALAPPDATA\Programs\MotionCommander"
    )

    foreach ($dir in $knownDirs) {
        if (Test-Path (Join-Path $dir "Win11CopyDialog.exe")) {
            $candidates += [PSCustomObject]@{
                Path = (Get-Item $dir).FullName.TrimEnd('\')
                KeyPath = $null
                DisplayName = "Motion Commander"
                Version = (Get-Item (Join-Path $dir "Win11CopyDialog.exe")).VersionInfo.FileVersion
            }
        }
    }

    if ($candidates.Count -gt 0) {
        return ($candidates | Group-Object -Property Path | ForEach-Object { $_.Group[0] })[0]
    }
    return $null
}

# 3. Определяем целевой каталог
$existing = Find-ExistingInstallation
$isUpgrade = ($null -ne $existing)

if ($isUpgrade) {
    $installDir = $existing.Path
    Write-Host "[ОБНОВЛЕНИЕ] Найдена ранее установленная версия Motion Commander!" -ForegroundColor Green
    Write-Host "  Каталог установки: $installDir" -ForegroundColor Cyan
    Write-Host "  Предыдущая версия: $($existing.Version)" -ForegroundColor Gray
    Write-Host "  Новая версия:      $appVersion" -ForegroundColor Yellow
    Write-Host "  Обновление выполняется на месте — дублирующая программа создаваться НЕ будет." -ForegroundColor Green
}
else {
    $isAdminRole = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if ($isAdminRole) {
        $installDir = "$env:ProgramFiles\Motion Commander"
    }
    else {
        $installDir = "$env:LOCALAPPDATA\Programs\Motion Commander"
    }
    Write-Host "[УСТАНОВКА] Ранее установленных копий не обнаружено." -ForegroundColor Cyan
    Write-Host "  Каталог для установки: $installDir" -ForegroundColor White
}

# 4. Проверяем права администратора, если установка идёт в системный каталог (Program Files)
$isSystemPath = ($installDir -like "$env:ProgramFiles*" -or $installDir -like "${env:ProgramFiles(x86)}*")
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if ($isSystemPath -and -not $isAdmin) {
    Write-Host ""
    Write-Host "[ТРЕБУЮТСЯ ПРАВА АДМИНИСТРАТОРА] Каталог $installDir защищён Windows." -ForegroundColor Yellow
    Write-Host "Запрос повышения привилегий (UAC)..." -ForegroundColor Yellow
    try {
        $proc = Start-Process powershell.exe -ArgumentList "-ExecutionPolicy Bypass -NoProfile -File `"$PSCommandPath`"" -Verb RunAs -Wait -PassThru -ErrorAction Stop
        exit $proc.ExitCode
    }
    catch {
        Write-Warning "Не удалось запросить повышение UAC: $($_.Exception.Message)"
        Write-Warning "Если доступ к $installDir будет заблокирован, запустите Install.cmd от имени администратора."
    }
}

# 5. Останавливаем запущенные процессы программы, чтобы файлы не были заблокированы
$runningProcs = Get-Process -Name "Win11CopyDialog", "motion", "MotionCommanderDiagnostics" -ErrorAction SilentlyContinue
if ($runningProcs) {
    Write-Host "Закрытие запущенных процессов Motion Commander перед обновлением..." -ForegroundColor Cyan
    $runningProcs | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 600
}

# 6. Создаём целевой каталог при необходимости
if (!(Test-Path $installDir)) {
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
}

# 7. Развёртывание бинарных файлов
Write-Host "Развёртывание файлов в $installDir..." -ForegroundColor Cyan

$hasCsproj = Test-Path "$repoRoot\Win11CopyDialog\Win11CopyDialog.csproj"

if ($hasCsproj) {
    # Сборка и публикация из исходников
    & dotnet publish "$repoRoot\Win11CopyDialog\Win11CopyDialog.csproj" -c Release -o $installDir
    & dotnet publish "$repoRoot\src\MotionCommander.Cli\MotionCommander.Cli.csproj" -c Release -o $installDir
    & dotnet publish "$repoRoot\src\MotionCommander.Diagnostics\MotionCommander.Diagnostics.csproj" -c Release -o $installDir
}
else {
    # Копирование из текущей папки / папки publish
    $sourceFilesDir = $PSScriptRoot
    if (Test-Path "$PSScriptRoot\Win11CopyDialog.exe") {
        $sourceFilesDir = $PSScriptRoot
    }
    elseif (Test-Path "$PSScriptRoot\..\Win11CopyDialog.exe") {
        $sourceFilesDir = (Resolve-Path "$PSScriptRoot\..").Path
    }
    elseif (Test-Path "$PSScriptRoot\publish") {
        $sourceFilesDir = "$PSScriptRoot\publish"
    }
    elseif (Test-Path "$repoRoot\dist\publish_v$appVersion") {
        $sourceFilesDir = "$repoRoot\dist\publish_v$appVersion"
    }

    if (Test-Path "$sourceFilesDir\Win11CopyDialog.exe") {
        $info = (Get-Item "$sourceFilesDir\Win11CopyDialog.exe").VersionInfo.FileVersion
        if ($info) { $appVersion = $info }
    }

    Get-ChildItem -Path $sourceFilesDir -Recurse | ForEach-Object {
        $rel = $_.FullName.Substring($sourceFilesDir.Length).TrimStart('\', '/')
        if (-not [string]::IsNullOrWhiteSpace($rel) -and $rel -notlike ".git*" -and $rel -notlike "*.ps1" -and $rel -notlike "*.cmd") {
            $dest = Join-Path $installDir $rel
            if ($_.PSIsContainer) {
                if (!(Test-Path $dest)) { New-Item -ItemType Directory -Path $dest -Force | Out-Null }
            }
            else {
                Copy-Item $_.FullName -Destination $dest -Force
            }
        }
    }
}

$exePath = Join-Path $installDir "Win11CopyDialog.exe"
if (-not (Test-Path $exePath)) {
    throw "Ошибка: главный исполняемый файл $exePath не найден после развёртывания!"
}

# 8. Очистка устаревших дубликатов
# Если обновляется основная программа в Program Files, удаляем старую запись в HKCU и старую папку в LocalAppData,
# чтобы в «Параметры -> Установленные приложения» не было двух одинаковых программ!
if ($isSystemPath) {
    $dupReg = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MotionCommander"
    if (Test-Path $dupReg) {
        Write-Host "Очистка дублирующей записи реестра пользователя ($dupReg)..." -ForegroundColor Gray
        Remove-Item -Path $dupReg -Recurse -Force -ErrorAction SilentlyContinue
    }

    $dupDir = "$env:LOCALAPPDATA\Programs\MotionCommander"
    if (Test-Path $dupDir) {
        Write-Host "Очистка дублирующего каталога в AppData ($dupDir)..." -ForegroundColor Gray
        Remove-Item -Path $dupDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# 9. Создание или обновление ярлыков
$wshell = New-Object -ComObject WScript.Shell

# Рабочий стол
$desktopDirs = @([Environment]::GetFolderPath('Desktop'))
$oneDriveDesktop = "$env:USERPROFILE\OneDrive\Desktop"
if (Test-Path $oneDriveDesktop) { $desktopDirs += $oneDriveDesktop }

foreach ($d in $desktopDirs) {
    if (Test-Path $d) {
        $shortcut = $wshell.CreateShortcut("$d\Motion Commander.lnk")
        $shortcut.TargetPath = $exePath
        $shortcut.WorkingDirectory = $installDir
        $shortcut.Description = "Motion Commander - Управление накопителями и быстрый файловый менеджер"
        $shortcut.IconLocation = "$exePath,0"
        $shortcut.Save()
    }
}

# Меню «Пуск»
$startMenuDir = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Motion Commander"
if (!(Test-Path $startMenuDir)) { New-Item -ItemType Directory -Path $startMenuDir -Force | Out-Null }
$startShortcut = $wshell.CreateShortcut("$startMenuDir\Motion Commander.lnk")
$startShortcut.TargetPath = $exePath
$startShortcut.WorkingDirectory = $installDir
$startShortcut.Description = "Motion Commander - Управление накопителями и быстрый файловый менеджер"
$startShortcut.IconLocation = "$exePath,0"
$startShortcut.Save()

# Ярлык консоли CLI в меню Пуск
$cliShortcut = $wshell.CreateShortcut("$startMenuDir\Motion Commander CLI.lnk")
$cliShortcut.TargetPath = (Join-Path $installDir "motion.exe")
$cliShortcut.WorkingDirectory = $installDir
$cliShortcut.Description = "Motion Commander CLI"
$cliShortcut.Save()

# 10. Регистрация в реестре Windows (только ОДНА запись, совместимая с Inno Setup)
$regAppId = "{D37D5726-2F1E-4B07-B25C-2150E697DF2A}_is1"
if ($isSystemPath) {
    $mainRegPath = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$regAppId"
}
else {
    $mainRegPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$regAppId"
}

try {
    if (!(Test-Path $mainRegPath)) { New-Item -Path $mainRegPath -Force | Out-Null }
    Set-ItemProperty -Path $mainRegPath -Name "DisplayName" -Value "Motion Commander $appVersion"
    Set-ItemProperty -Path $mainRegPath -Name "DisplayVersion" -Value $appVersion
    Set-ItemProperty -Path $mainRegPath -Name "Publisher" -Value "BlackTecCom - Jaborov Daler"
    Set-ItemProperty -Path $mainRegPath -Name "InstallLocation" -Value $installDir
    Set-ItemProperty -Path $mainRegPath -Name "DisplayIcon" -Value "$exePath,0"

    $uninsExe = Join-Path $installDir "unins000.exe"
    if (Test-Path $uninsExe) {
        Set-ItemProperty -Path $mainRegPath -Name "UninstallString" -Value "`"$uninsExe`""
    }
    else {
        Set-ItemProperty -Path $mainRegPath -Name "UninstallString" -Value "powershell -Command Remove-Item '$installDir' -Recurse -Force; Remove-Item '$mainRegPath' -Force"
    }
}
catch { }

# 11. Добавление в системный PATH
try {
    $pathScope = if ($isSystemPath -and $isAdmin) { "Machine" } else { "User" }
    $currentEnvPath = [Environment]::GetEnvironmentVariable("Path", $pathScope)
    if ($currentEnvPath -notlike "*$installDir*") {
        [Environment]::SetEnvironmentVariable("Path", "$currentEnvPath;$installDir", $pathScope)
        Write-Host "Каталог добавлен в переменную PATH ($pathScope)." -ForegroundColor Gray
    }
}
catch { }

Write-Host ""
Write-Host "==========================================================" -ForegroundColor Green
if ($isUpgrade) {
    Write-Host "   УСПЕШНО: Motion Commander обновлён до v$appVersion!     " -ForegroundColor Green
} else {
    Write-Host "   УСПЕШНО: Motion Commander v$appVersion установлен!      " -ForegroundColor Green
}
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "  Каталог программы: $installDir" -ForegroundColor White
Write-Host "  Версия в системе:  $appVersion" -ForegroundColor White
Write-Host "  Ярлыки:            Рабочий стол и меню «Пуск»" -ForegroundColor White
Write-Host "  Команда в консоли: 'motion'" -ForegroundColor White
Write-Host "==========================================================" -ForegroundColor Cyan
