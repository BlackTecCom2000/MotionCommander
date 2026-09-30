<#
.SYNOPSIS
    Подписывает выпущенные файлы Motion Commander.

.DESCRIPTION
    Сертификата подписи у проекта нет: купить его может только владелец
    аккаунта. Пока его нет, скрипт не подписывает ничего и объясняет,
    что нужно сделать, — молча выйти из строя было бы хуже.

    Windows помечает неподписанные файлы как неопознанные, и
    пользователь привыкает подтверждать запуск вручную. Привычка
    нажимать «Выполнить в любом случае» опаснее самого предупреждения,
    потому что она распространяется и на действительно чужие файлы.

.EXAMPLE
    .\scripts\Sign-Release.ps1 -Thumbprint 0123456789ABCDEF...
#>
[CmdletBinding()]
param(
    [string] $Thumbprint,
    [string] $CertificatePath,
    [SecureString] $CertificatePassword,
    [string] $TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$distDir = Join-Path $repoRoot 'dist'

Write-Host 'ПОДПИСЬ АРТЕФАКТОВ' -ForegroundColor Cyan
Write-Host ('=' * 60)

# Файлы берутся из папки выпуска, а не из корня: подписывать нужно то,
# что действительно отдаётся пользователю.
$targets = Get-ChildItem -Path $distDir -Include '*.exe', '*.zip' -Recurse -File |
           Where-Object { $_.Name -like '*v3.8*' }

if (-not $targets) {
    throw 'В папке dist нет выпущенных файлов. Сначала выполните Publish-Release.ps1.'
}

if (-not $Thumbprint -and -not $CertificatePath) {
    Write-Host ''
    Write-Host 'ПОДПИСАТЬ НЕЧЕМ: не указан ни отпечаток, ни файл сертификата.' -ForegroundColor Yellow
    Write-Host ''
    Write-Host 'Нужен сертификат подписи кода. Варианты:' -ForegroundColor Yellow
    Write-Host '  1. Купить сертификат подписи кода (требуется проверка личности).' -ForegroundColor Yellow
    Write-Host '  2. Использовать бесплатный сертификат на время разработки.' -ForegroundColor Yellow
    Write-Host '     Он не подходит для публикации в интернете: срок и область' -ForegroundColor Yellow
    Write-Host '     применения ограничены и статус будет «не доверенный».'
    Write-Host ''
    Write-Host 'Где взять сертификат в Windows:'
    Write-Host '  certmgr.msc -> Запросить новый сертификат -> Параметры проверки кода'
    Write-Host ''
    Write-Host 'После получения повторите с указанием отпечатка:' -ForegroundColor Yellow
    Write-Host ('  .\scripts\Sign-Release.ps1 -Thumbprint ' + 'ОТПЕЧАТОК')
    Write-Host ''
    Write-Host 'До подписи целостность обновлений подтверждается манифестом'
    Write-Host 'dist\checksums.json, который проверяется перед применением.'
    Write-Host 'Это защита от повреждения и подмены файла, но не подтверждение'
    Write-Host 'издателя: манифест сам не подписан.'
    exit 0
}

$signed = 0
$failed = 0

foreach ($file in $targets) {
    $args = @('sign', $file.FullName)

    if ($Thumbprint) {
        $args += @('/sha1', $Thumbprint, '/fd', 'sha256', '/tr', $TimestampUrl, '/td', 'sha256')
    }
    else {
        $args += @('/f', $CertificatePath, '/fd', 'sha256', '/tr', $TimestampUrl, '/td', 'sha256')
    }

    if ($CertificatePassword) {
        $plain = [System.Net.NetworkCredential]::new('', $CertificatePassword).Password
        $args += @('/p', $plain)
    }

    & signtool.exe @args

    if ($LASTEXITCODE -eq 0) {
        Write-Host ('  подписан: ' + $file.Name) -ForegroundColor Green
        $signed++
    }
    else {
        Write-Host ('  ОТКАЗ: ' + $file.Name + ' (код ' + $LASTEXITCODE + ')') -ForegroundColor Red
        $failed++
    }
}

Write-Host ''
Write-Host ('подписано: ' + $signed + ', отказано: ' + $failed)
Write-Host ''
Write-Host 'ВАЖНО: подпись файла сама по себе не мешает подмене. Чтобы подмена'
Write-Host 'обнаруживалась, программа должна проверять издателя подписи при'
Write-Host 'применении обновления. Эта проверка в проекте не сделана: манифест'
Write-Host 'контрольных сумм подтверждает содержимое, но не издателя.'

if ($failed -gt 0) { exit 1 }
exit 0
