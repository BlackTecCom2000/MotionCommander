<#
.SYNOPSIS
    Сборка кроссплатформенных пакетов Motion Commander CLI.

.DESCRIPTION
    Заменяет GitHub Actions, который не запускается на стороне репозитория
    (джобы падают до первого шага — обычно исчерпание минут Actions).

    Windows не может собрать linux-x64 и macos-arm64 напрямую, поэтому
    скрипт делает две вещи:

      1. Локально собирает пакет для текущей ОС (Windows) — это работает.
      2. Для остальных платформ готовит корректные .csproj и список команд,
         которые нужно выполнить на соответствующей машине или в CI.

    Также скрипт собирает диагностический отчёт о том, почему Actions не
    работает, чтобы это было видно сразу, а не через неделю.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\scripts\Build-CrossPlatform.ps1
    powershell -ExecutionPolicy Bypass -File .\scripts\Build-CrossPlatform.ps1 -Push
#>
param(
    [string]$Version = "",
    [string]$OutputRoot = "",
    [switch]$Push
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

if ([string]::IsNullOrWhiteSpace($Version)) {
    $manifestPath = Join-Path $repoRoot "version.json"
    if (Test-Path $manifestPath) {
        $Version = (Get-Content $manifestPath -Raw | ConvertFrom-Json).version
    } else {
        $Version = "0.0.0"
    }
}

$cliProject = Join-Path $repoRoot "src\MotionCommander.Cli\MotionCommander.Cli.csproj"
$coreProject = Join-Path $repoRoot "src\MotionCommander.Core\MotionCommander.Core.csproj"

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "dist\cli"
}
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  Motion Commander CLI: кроссплатформенная сборка v$Version" -ForegroundColor Yellow
Write-Host "==========================================================" -ForegroundColor Cyan

# ── 1. Диагностика GitHub Actions ────────────────────────────────────────────
Write-Host "`n[1/4] Проверка состояния GitHub Actions..." -ForegroundColor Cyan

$actionsNote = "не проверялся (нет доступа к GitHub API)"
try
{
    $headers = @{ 'User-Agent' = 'MotionCommander-Build' }
    $repoInfo = Read-Host "GitHub репозиторий (Enter = BlackTecCom2000/MotionCommander)" -ErrorAction SilentlyContinue
    if ([string]::IsNullOrWhiteSpace($repoInfo)) { $repoInfo = "BlackTecCom2000/MotionCommander" }

    $runs = Invoke-RestMethod -Headers $headers -Uri "https://api.github.com/repos/$repoInfo/actions/runs?per_page=1" -TimeoutSec 30
    $last = $runs.workflow_runs[0]

    Write-Host "  Последний запуск: $($last.head_branch) / $($last.head_sha.Substring(0,7))" -ForegroundColor DarkGray
    Write-Host "  Статус: $($last.status) / $($last.conclusion)" -ForegroundColor $(if ($last.conclusion -eq 'success') { 'Green' } else { 'Red' })

    if ($last.conclusion -eq 'failure')
    {
        Write-Host ""
        Write-Host "  ВНИМАНИЕ: GitHub Actions падает. Если ни один шаг не был" -ForegroundColor Yellow
        Write-Host "  выполнен (steps: 0), джобы не смогли получить раннер." -ForegroundColor Yellow
        Write-Host "  Наиболее вероятная причина: исчерпаны бесплатные минуты" -ForegroundColor Yellow
        Write-Host "  Actions или Actions отключены в настройках репозитория." -ForegroundColor Yellow
        Write-Host "  Проверьте: Settings -> Actions -> General." -ForegroundColor Yellow
    }
    $actionsNote = "$($last.head_branch): $($last.conclusion)"
}
catch
{
    Write-Host "  Не удалось проверить Actions: $($_.Exception.Message)" -ForegroundColor DarkGray
}

# ── 2. Проверка наличия .NET SDK и работоспособности RID-публикации ──────────
Write-Host "`n[2/4] Проверка .NET SDK..." -ForegroundColor Cyan
$sdk = & dotnet --version 2>$null
if (-not $sdk) { throw ".NET SDK не найден. Установите .NET 8 SDK." }
Write-Host "  SDK: $sdk" -ForegroundColor DarkGray

$sdks = & dotnet --list-sdks
Write-Host "  Доступные SDK:" -ForegroundColor DarkGray
$sdks | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }

# ── 3. Сборка пакета для текущей ОС ──────────────────────────────────────────
Write-Host "`n[3/4] Сборка пакета для текущей ОС..." -ForegroundColor Cyan

# Определение ОС.
# ВАЖНО: Windows PowerShell 5.1 возвращает в $PSVersionTable.OS значение
# "Windows_NT", а PowerShell 7 — "Windows". Раньше проверялось только
# "Windows", поэтому на типичной системе с Windows PowerShell скрипт
# не собирал локальный пакет вообще.
$osName = ''
if ($PSVersionTable.OS) { $osName = [string]$PSVersionTable.OS }
if ([string]::IsNullOrWhiteSpace($osName) -and $env:OS) { $osName = $env:OS }

$isWindows = $osName -like 'Windows*'
$isLinux = $osName -eq 'Linux'
$isDarwin = $osName -eq 'Darwin'

$runtimeId = $null
if ($isWindows) {
    $runtimeId = 'win-x64'
}
elseif ($isLinux) {
    $runtimeId = 'linux-x64'
}
elseif ($isDarwin) {
    # Apple Silicon против Intel: на Darwin архитектуру проверяем через
    # переменную окружения, которую задаёт оболочка.
    if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { $runtimeId = 'osx-arm64' }
    elseif ($env:MOTION_ARCH -eq 'arm64') { $runtimeId = 'osx-arm64' }
    else { $runtimeId = 'osx-x64' }
}

if (-not $runtimeId) {
    Write-Host "  Неизвестная ОС, пропускаем локальную сборку." -ForegroundColor Yellow
}
else
{
    $outDir = Join-Path $OutputRoot $runtimeId
    if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }

    Write-Host "  RID: $runtimeId" -ForegroundColor DarkGray
    & dotnet publish $cliProject -c Release -r $runtimeId --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -o $outDir --nologo
    if ($LASTEXITCODE -ne 0) { throw "Сборка для $runtimeId провалилась." }

    # На Windows публикуемый бинарник имеет расширение .exe
    $exeName = "motion"
    if ($isWindows) { $exeName = "motion.exe" }

    $binary = Join-Path $outDir $exeName
    if (Test-Path $binary) {
        $sizeMb = [Math]::Round((Get-Item $binary).Length / 1MB, 2)
        Write-Host "  Готово: $binary ($sizeMb MB)" -ForegroundColor Green
    } else {
        Write-Host "  Внимание: бинарник motion не найден в $outDir" -ForegroundColor Yellow
    }
}

# ── 4. Инструкции по сборке остальных платформ ──────────────────────────────
Write-Host "`n[4/4] Сборка остальных платформ..." -ForegroundColor Cyan

$targets = @(
    @{ rid = 'linux-x64';   os = 'Linux / macOS с .NET 8'; exe = 'motion' },
    @{ rid = 'linux-arm64'; os = 'Linux ARM64 (Raspberry Pi и пр.)'; exe = 'motion' },
    @{ rid = 'osx-arm64';   os = 'macOS Apple Silicon (M1-M4)'; exe = 'motion' },
    @{ rid = 'osx-x64';     os = 'macOS Intel'; exe = 'motion' }
)

$currentRid = $runtimeId
$skip = @($targets | Where-Object { $_.rid -eq $currentRid })

foreach ($t in $targets) {
    if ($t.rid -eq $currentRid) { continue }

    $outDir = Join-Path $OutputRoot $t.rid
    Write-Host ""
    Write-Host "  $($t.rid): $($t.os)" -ForegroundColor Cyan
    Write-Host "    dotnet publish src/MotionCommander.Cli/MotionCommander.Cli.csproj -c Release -r $($t.rid) --self-contained true -p:PublishSingleFile=true -o dist/cli/$($t.rid)" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "  ПРИМЕЧАНИЕ: Windows не умеет собирать linux-* и osx-*. Эти" -ForegroundColor Yellow
Write-Host "  цели требуют запуска на соответствующей ОС (или в CI с рабочим" -ForegroundColor Yellow
Write-Host "  Actions). Скрипт подготовил команды — выполните их на нужных" -ForegroundColor Yellow
Write-Host "  машинах, чтобы получить полный набор пакетов." -ForegroundColor Yellow

# ── 5. Итоговая сводка ───────────────────────────────────────────────────────
Write-Host ""
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  ИТОГ" -ForegroundColor Yellow
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  Версия:        $Version"
Write-Host "  GitHub Actions: $actionsNote"

$built = Get-ChildItem $OutputRoot -Directory -ErrorAction SilentlyContinue
if ($built) {
    Write-Host "  Собрано RID:   $(($built | ForEach-Object { $_.Name }) -join ', ')"
} else {
    Write-Host "  Собрано RID:   ничего"
}
Write-Host "  Каталог:       $OutputRoot"

if ($Push) {
    Write-Host ""
    Write-Host "  Публикация на GitHub..." -ForegroundColor Cyan
    & git add -A
    & git commit -m "build: cross-platform CLI packages for v$Version"
    if ($LASTEXITCODE -eq 0) {
        & git push origin main
    } else {
        Write-Host "  Коммит не создан (возможно, нет изменений)" -ForegroundColor Yellow
    }
}

Write-Host "==========================================================" -ForegroundColor Green
Write-Host "  Готово" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
