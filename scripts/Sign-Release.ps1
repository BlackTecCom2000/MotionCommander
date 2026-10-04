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

function Find-SignTool {
    <#
        Ищет signtool.exe там, где Windows его действительно кладёт.

        В PATH его почти никогда нет: инструмент поставляется с
        комплектом разработчика Windows, а не с системой, и путь к нему
        не прописывается. Без поиска скрипт падал бы с «не является
        командой», и владелец решил бы, что подпись не работает.
    #>
    $onPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    $roots = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "$env:ProgramFiles\Windows Kits\10\bin"
    ) | Where-Object { Test-Path $_ }

    foreach ($root in $roots) {
        # Берётся самая свежая версия: каталоги называются по версии
        # набора, и старые могут быть неполными.
        $dirs = Get-ChildItem $root -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -match '^10\.' } |
                Sort-Object { [version]($_.Name) } -Descending

        foreach ($d in $dirs) {
            foreach ($arch in @('x64', 'x86')) {
                $candidate = Join-Path $d.FullName "$arch\signtool.exe"
                if (Test-Path $candidate) { return $candidate }
            }
        }
    }

    return $null
}

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
    Write-Host '  1. БЕСПЛАТНО для этого проекта — SignPath Foundation.' -ForegroundColor Yellow
    Write-Host '     Репозиторий публичный и под MIT, то есть условия' -ForegroundColor Yellow
    Write-Host '     подходят. Подпись настоящая, издатель известен —' -ForegroundColor Yellow
    Write-Host '     именно этого требует Smart App Control.' -ForegroundColor Yellow
    Write-Host '     Заявка: signpath.org' -ForegroundColor Yellow
    Write-Host '  2. Купить сертификат (проверка личности, платно):' -ForegroundColor Yellow
    Write-Host '     DigiCert, Sectigo, Certum и подобные.' -ForegroundColor Yellow
    Write-Host ''
    Write-Host 'Самоподписанный сертификат НЕ решает задачу:' -ForegroundColor Red
    Write-Host '  Smart App Control и SmartScreen считают его неизвестным' -ForegroundColor Red
    Write-Host '  издателем и блокируют программу так же, как сейчас.' -ForegroundColor Red
    Write-Host ''
    Write-Host 'Дополнительно нужен signtool.exe (Windows Kits) — сейчас'
    Write-Host 'его на машине нет, но скрипт ищет его сам.'
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

$signTool = Find-SignTool

if (-not $signTool) {
    throw @'
signtool.exe не найден.

Инструмент подписи входит в комплект разработчика Windows и в систему
не входит. Установите его: Visual Studio с компонентой "Средства
разработки для Windows" либо отдельно Windows SDK.

После установки инструмент появляется в
  C:\Program Files (x86)\Windows Kits\10\bin\<версия>\x64\signtool.exe
и скрипт найдёт его сам.
'@
}

Write-Host ''
Write-Host ("Инструмент подписи: " + $signTool)
Write-Host ''

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

    & $signTool @args

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
