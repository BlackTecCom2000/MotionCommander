param(
    [string]$Version = "3.8.31",
    [string[]]$Notes = $null,
    [switch]$SkipBuild,
    [switch]$SkipPush
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

if ($null -eq $Notes -or $Notes.Count -eq 0) {
    $Notes = @(
        "Fixed: the download manager no longer crashes on open. A progress bar was bound two-way to a read-only property, which threw a fatal error the moment the screen was shown",
        "Fixed: invalid XAML in the download manager (unescaped braces in a format string) that would have broken the view",
        "Added --view-audit: creates and lays out all 17 screens and reports binding and markup errors instead of crashing, so this class of defect is caught in automation",
        "The new audit populates lists with real data, because an empty collection never applies item templates and therefore never activates the bindings inside them",
        "Data-loss prevention, bounded process waits and UI-thread offload, as described in 3.8.21"
    )
}

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "   Motion Commander: Publish Release v$Version            " -ForegroundColor Yellow
Write-Host "==========================================================" -ForegroundColor Cyan

$cleanVer = $Version.TrimStart('v', 'V')
$buildVer = "$cleanVer.0"

# 1. Update csproj files
Write-Host "[1/6] Updating csproj versions..." -ForegroundColor Cyan
$csprojs = @(
    "$repoRoot\Win11CopyDialog\Win11CopyDialog.csproj",
    "$repoRoot\src\MotionCommander.Core\MotionCommander.Core.csproj",
    "$repoRoot\src\MotionCommander.Cli\MotionCommander.Cli.csproj"
)

foreach ($proj in $csprojs) {
    if (Test-Path $proj) {
        $content = Get-Content $proj -Raw
        $content = [System.Text.RegularExpressions.Regex]::Replace($content, "<Version>[^<]+</Version>", "<Version>$cleanVer</Version>")
        $content = [System.Text.RegularExpressions.Regex]::Replace($content, "<AssemblyVersion>[^<]+</AssemblyVersion>", "<AssemblyVersion>$buildVer</AssemblyVersion>")
        $content = [System.Text.RegularExpressions.Regex]::Replace($content, "<FileVersion>[^<]+</FileVersion>", "<FileVersion>$buildVer</FileVersion>")
        Set-Content -Path $proj -Value $content -NoNewline
    }
}

# 2. Release build
if (!$SkipBuild) {
    Write-Host "[2/6] Building solution in Release mode..." -ForegroundColor Cyan
    & dotnet build "$repoRoot\MotionCommander.sln" -c Release
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed for MotionCommander.sln"
    }
}

# 3. Publish binaries and package portable ZIP in dist/
Write-Host "[3/7] Publishing binaries..." -ForegroundColor Cyan
$distDir = "$repoRoot\dist"
$publishDir = "$distDir\publish"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

& dotnet publish "$repoRoot\Win11CopyDialog\Win11CopyDialog.csproj" -c Release -o $publishDir
& dotnet publish "$repoRoot\src\MotionCommander.Cli\MotionCommander.Cli.csproj" -c Release -o $publishDir

# Диагностический инструмент публикуется рядом с программой.
#
# Он нужен пользователю и в сборочном конвейере: собран без манифеста
# администратора и потому запускается без подтверждения в диалоге UAC,
# в отличие от основной программы. Публикуется после неё, чтобы при
# общей папке его файлы не оказались затёртыми одноимёнными.
& dotnet publish "$repoRoot\src\MotionCommander.Diagnostics\MotionCommander.Diagnostics.csproj" -c Release -o $publishDir

if (-not (Test-Path "$publishDir\MotionCommanderDiagnostics.exe")) {
    throw "Диагностический инструмент не опубликован: $publishDir\MotionCommanderDiagnostics.exe отсутствует"
}

Write-Host "Packaging portable ZIP archives..." -ForegroundColor Cyan
$zipFile = "$distDir\MotionCommander-v$cleanVer-Portable.zip"
$latestZip = "$distDir\MotionCommander-Latest-Portable.zip"

if (Test-Path $zipFile) { Remove-Item $zipFile -Force }
if (Test-Path $latestZip) { Remove-Item $latestZip -Force }

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($publishDir, $zipFile, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Copy-Item $zipFile -Destination $latestZip -Force

$zipMb = [Math]::Round((Get-Item $zipFile).Length / 1MB, 2)
Write-Host "Portable archive created: $zipFile ($zipMb MB)" -ForegroundColor Green

Write-Host "Packaging lightweight Delta Patch ZIP..." -ForegroundColor Cyan
$patchStagingDir = "$distDir\patch_staging"
if (Test-Path $patchStagingDir) { Remove-Item $patchStagingDir -Recurse -Force }
New-Item -ItemType Directory -Path $patchStagingDir -Force | Out-Null

# ВАЖНО: раньше здесь стоял `Get-ChildItem -Path $publishDir -File`, который
# брал ТОЛЬКО файлы верхнего уровня и молча терял все подпапки.
# UpdateService.ApplySeamlessUpdate копирует staging рекурсивно
# (SearchOption.AllDirectories) и сам создаёт недостающие каталоги,
# поэтому отсутствие подпапок означало бы сломанную сборку у обновившихся.
#
# Папка runtimes\ весит ~82 МБ, потому что туда попадают нативные библиотеки
# для ВСЕХ платформ (android*, ios*, browser-wasm, linux*, osx*).
# Исключать её целиком тоже нельзя: приложению на Windows обязательно нужны
#   runtimes\win-x64\native\e_sqlite3.dll      — нативная SQLite (базы, драйверы)
#   runtimes\win\lib\net8.0\System.Management.dll — WMI для инспектора драйверов
# Поэтому в дельту идут только Windows-варианты (~2.2 МБ вместо 82 МБ).
Get-ChildItem -Path $publishDir -Recurse -File | ForEach-Object {
    $relative = $_.FullName.Substring($publishDir.Length).TrimStart('\', '/')

    if ($relative -like 'runtimes\*') {
        $rid = ($relative -split '\\')[1]
        if ($rid -ne 'win' -and $rid -ne 'win-x64') { return }
    }

    $target = Join-Path $patchStagingDir $relative
    $targetDir = Split-Path -Parent $target
    if (-not (Test-Path $targetDir)) {
        New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
    }
    Copy-Item $_.FullName -Destination $target -Force
}

$patchFile = "$distDir\MotionCommander-v$cleanVer-Patch.zip"
$latestPatch = "$distDir\MotionCommander-Latest-Patch.zip"
if (Test-Path $patchFile) { Remove-Item $patchFile -Force }
if (Test-Path $latestPatch) { Remove-Item $latestPatch -Force }

[System.IO.Compression.ZipFile]::CreateFromDirectory($patchStagingDir, $patchFile, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Copy-Item $patchFile -Destination $latestPatch -Force
Remove-Item $patchStagingDir -Recurse -Force

$patchMb = [Math]::Round((Get-Item $patchFile).Length / 1MB, 2)
Write-Host "Delta Patch archive created: $patchFile ($patchMb MB)" -ForegroundColor Green

# 4. Compile Inno Setup Windows Installer (.exe)
Write-Host "[4/7] Compiling Windows Installer (.exe)..." -ForegroundColor Cyan
$isccPaths = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
)
$iscc = $isccPaths | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    $cmd = Get-Command iscc -ErrorAction SilentlyContinue
    if ($cmd) { $iscc = $cmd.Source }
}

$issFile = "$repoRoot\installer\MotionCommander.iss"
# Флаг реального наличия инсталлятора. Раньше setupExeUrl попадал в
# манифест безусловно, и если ISCC.exe не установлен, приложение
# предлагало пользователю скачать файл, которого в репозитории нет.
$setupCreated = $false
if ($iscc -and (Test-Path $issFile)) {
    & $iscc "/DMyAppVersion=$cleanVer" $issFile
    if ($LASTEXITCODE -eq 0) {
        $setupExe = "$distDir\MotionCommander-v$cleanVer-Setup.exe"
        $latestSetup = "$distDir\MotionCommander-Latest-Setup.exe"
        if (Test-Path $setupExe) {
            Copy-Item $setupExe -Destination $latestSetup -Force
            $setupMb = [Math]::Round((Get-Item $setupExe).Length / 1MB, 2)
            $setupCreated = $true
            Write-Host "Installer created: $setupExe ($setupMb MB)" -ForegroundColor Green
        }
    } else {
        Write-Warning "Inno Setup compilation exited with code $LASTEXITCODE"
        # Скомпилированный ранее инсталлятор другой версии нельзя выдавать
        # за текущий: он установил бы прежнюю сборку под новым именем.
        $staleSetup = "$distDir\MotionCommander-v$cleanVer-Setup.exe"
        if (Test-Path $staleSetup) {
            Remove-Item $staleSetup -Force
            Write-Warning "Removed stale installer: $staleSetup" -ForegroundColor Yellow
        }
    }
} else {
    Write-Warning "Inno Setup compiler (ISCC.exe) not found. Skipping installer generation."
}

# 5. Update version.json
Write-Host "[5/7] Updating version.json manifest..." -ForegroundColor Cyan
$versionManifest = [ordered]@{
    version = $cleanVer
    releaseDate = (Get-Date).ToString("yyyy-MM-dd")
    productName = "Motion Commander"
    author = "BlackTecCom - Jaborov Daler"
    license = "MIT"
    minWindowsVersion = "10.0.19041"
    changelog = $Notes
    patchUrl = "https://raw.githubusercontent.com/BlackTecCom2000/MotionCommander/main/dist/MotionCommander-v$cleanVer-Patch.zip"
    patchSizeMb = $patchMb
    downloadUrl = "https://raw.githubusercontent.com/BlackTecCom2000/MotionCommander/main/dist/MotionCommander-v$cleanVer-Portable.zip"
    installerUrl = "https://raw.githubusercontent.com/BlackTecCom2000/MotionCommander/main/dist/MotionCommander-v$cleanVer-Portable.zip"
}

if ($setupCreated) {
    # setupExeUrl добавляется ТОЛЬКО если инсталлятор действительно собран.
    # Иначе приложение предложило бы скачать несуществующий файл.
    $versionManifest["setupExeUrl"] = "https://raw.githubusercontent.com/BlackTecCom2000/MotionCommander/main/dist/MotionCommander-v$cleanVer-Setup.exe"
} else {
    # Write-Warning в PowerShell 5.1 не принимает -ForegroundColor:
    # передача ключа роняла весь скрипт с InvalidArgument.
    Write-Warning "setupExeUrl omitted from version.json: installer was not built."
}

$jsonStr = $versionManifest | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText("$repoRoot\version.json", $jsonStr, [System.Text.Encoding]::UTF8)

# 5b. Manifest of checksums
#
# The program verifies the SHA-256 of the downloaded update against this
# file before unpacking it. Without it, anything able to substitute the
# download (a hijacked address, a replaced response, a poisoned CDN cache)
# would run its own code with administrator rights, because the whole
# archive is executed elevated.
#
# The manifest itself is not signed: an attacker able to replace both the
# archive and this file replaces them consistently and the check passes.
# What is closed here is substitution of the archive alone. Closing the
# rest requires a signing certificate, which the project does not have.
Write-Host "[5b/7] Writing dist\checksums.json..." -ForegroundColor Cyan

$checksums = [ordered]@{
    version = $cleanVer
    releaseDate = (Get-Date).ToString("yyyy-MM-dd")
    algorithm = "SHA-256"
    signed = $false
    files = [ordered]@{}
}

foreach ($artifact in @(
    @{ name = "MotionCommander-v$cleanVer-Portable.zip"; path = $zipFile },
    @{ name = "MotionCommander-v$cleanVer-Patch.zip";   path = $patchFile },
    @{ name = "MotionCommander-v$cleanVer-Setup.exe";   path = $setupExe }
)) {
    if ([string]::IsNullOrWhiteSpace($artifact.path) -or -not (Test-Path $artifact.path)) {
        continue
    }
    $hash = (Get-FileHash -Path $artifact.path -Algorithm SHA256).Hash.ToUpperInvariant()
    $checksums.files[$artifact.name] = $hash
    Write-Host "  $($artifact.name) = $hash"
}

if ($checksums.files.Count -eq 0) {
    throw "checksums.json would be empty: no release artifacts were found to hash."
}

$checksumJson = $checksums | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText("$distDir\checksums.json", $checksumJson, [System.Text.Encoding]::UTF8)

# 6. Update local install directory
$localInstallDir = "$env:LOCALAPPDATA\Programs\MotionCommander"
if (Test-Path $localInstallDir) {
    Write-Host "[6/7] Updating local installation at $localInstallDir..." -ForegroundColor Cyan
    try {
        Copy-Item "$publishDir\*" -Destination $localInstallDir -Recurse -Force -ErrorAction SilentlyContinue
    } catch {
        Write-Warning "Some files in $localInstallDir are locked and will be updated on app restart."
    }
}

# 7. Git commit, tag, and push
if (!$SkipPush) {
    & git add -A
    $commitMsg = "release: v$cleanVer - " + ($Notes -join "; ")
    & git commit -m $commitMsg
    
    $existingTag = & git tag -l "v$cleanVer"
    if ($existingTag) {
        & git tag -d "v$cleanVer"
    }
    & git tag -a "v$cleanVer" -m "Motion Commander v$cleanVer Release"
    
    & git push origin main
    & git push origin "v$cleanVer" --force
    Write-Host "Successfully pushed to GitHub! Release v$cleanVer is live." -ForegroundColor Green
}

Write-Host "==========================================================" -ForegroundColor Green
Write-Host "   Release v$cleanVer successfully published and ready!   " -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green

