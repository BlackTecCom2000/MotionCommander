<#
.SYNOPSIS
    Отправляет подготовленный релиз Motion Commander в GitHub.

.DESCRIPTION
    Скрипт выполняет финальный шаг деплоя: push ветки main и тега версии
    в origin. Требуется однократная авторизация GitHub — при первом запуске
    Git Credential Manager откроет браузер для входа.

    Использование:
        powershell -ExecutionPolicy Bypass -File .\scripts\Push-Release.ps1
        powershell -ExecutionPolicy Bypass -File .\scripts\Push-Release.ps1 -Version 3.8.18
#>
param(
    [string]$Version = "",
    [string]$Remote = "origin"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

# Git не всегда присутствует в PATH текущей сессии, хотя установлен в системе.
$gitExe = Get-Command git -ErrorAction SilentlyContinue
if (-not $gitExe) {
    $candidates = @(
        "C:\Program Files\Git\cmd\git.exe",
        "${env:ProgramFiles(x86)}\Git\cmd\git.exe",
        "$env:LOCALAPPDATA\Programs\Git\cmd\git.exe"
    )
    $found = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $found) { throw "Git не найден. Установите Git for Windows." }
    $env:PATH = (Split-Path -Parent $found) + ";" + $env:PATH
}

# Если версия не указана, берём последний тег.
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = (& git tag --sort=-v:refname | Select-Object -First 1) -replace '^v',''
}
if ([string]::IsNullOrWhiteSpace($Version)) { throw "Не удалось определить версию релиза." }

$tag = "v$Version"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "   Motion Commander: публикация релиза $tag" -ForegroundColor Yellow
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Проверяем, что рабочее дерево чистое и всё закоммичено.
$dirty = & git status --porcelain --untracked-files=no
if ($dirty) {
    Write-Host "Есть незакоммиченные изменения:" -ForegroundColor Red
    $dirty | ForEach-Object { Write-Host "   $_" -ForegroundColor Red }
    throw "Сначала закоммитьте изменения: git add -A; git commit -m '...'"
}

# 2. Проверяем наличие коммита для тега.
& git rev-parse --verify "refs/tags/$tag" 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
    & git tag -a $tag -m "Motion Commander $tag"
    if ($LASTEXITCODE -ne 0) { throw "Не удалось создать тег $tag" }
    Write-Host "Создан тег $tag" -ForegroundColor Green
}

# 3. Проверяем, что артефакты релиза присутствуют в dist/ — без них
#    автообновление у пользователей покажет 404.
$dist = Join-Path $repoRoot "dist"
$required = @(
    "MotionCommander-$tag-Portable.zip",
    "MotionCommander-$tag-Patch.zip",
    "MotionCommander-$tag-Setup.exe"
)
$missing = @()
foreach ($f in $required) {
    $p = Join-Path $dist $f
    if (-not (Test-Path $p)) { $missing += $f }
    else {
        $mb = [Math]::Round((Get-Item $p).Length / 1MB, 2)
        Write-Host ("  ✓ {0} ({1} MB)" -f $f, $mb) -ForegroundColor DarkGray
    }
}
if ($missing.Count -gt 0) {
    Write-Host "Отсутствуют артефакты: $($missing -join ', ')" -ForegroundColor Red
    Write-Host "Соберите их командой:" -ForegroundColor Yellow
    Write-Host "  .\scripts\Publish-Release.ps1 -Version $Version" -ForegroundColor Yellow
    throw "Релиз неполон"
}

# 4. Публикуем ветку и тег.
Write-Host "Отправляю ветку main..." -ForegroundColor Cyan
& git push $Remote main
if ($LASTEXITCODE -ne 0) { throw "Не удалось отправить ветку main" }

Write-Host "Отправляю тег $tag..." -ForegroundColor Cyan
& git push $Remote $tag
if ($LASTEXITCODE -ne 0) { throw "Не удалось отправить тег $tag" }

Write-Host ""
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "   Релиз $tag опубликован!" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
Write-Host ""
Write-Host "Проверьте, что файлы доступны по адресам:" -ForegroundColor Cyan
$base = "https://raw.githubusercontent.com/BlackTecCom2000/MotionCommander/main/dist"
foreach ($f in $required) {
    Write-Host "  $base/$f" -ForegroundColor DarkGray
}
Write-Host ""
Write-Host "Через 1-3 минуты обновление появится у пользователей," -ForegroundColor Cyan
Write-Host "а на странице Releases появится тег $tag." -ForegroundColor Cyan
