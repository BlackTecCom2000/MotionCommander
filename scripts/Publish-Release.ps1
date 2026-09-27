param(
    [string]$Version = "3.8.21",
    [string[]]$Notes = $null,
    [switch]$SkipBuild,
    [switch]$SkipPush
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

if ($null -eq $Notes -or $Notes.Count -eq 0) {
    $Notes = @(
        "CRITICAL FIX: moving a folder could delete it without copying anything. A single unreadable subfolder made the file list empty, which was reported as success, and the source was then deleted",
        "CRITICAL FIX: sources are now only deleted after verifying that every file was actually copied and checked. Skipped files, cancelled copies and failed scans all keep their source",
        "CRITICAL FIX: two files with the same name no longer overwrite each other. The second used to destroy the first, and moving then deleted both originals",
        "CRITICAL FIX: existing files at the destination are never silently overwritten. A replaced file is kept with a timestamp suffix",
        "CRITICAL FIX: copying now writes to a .partial file, verifies the length, and only then moves it into place. Cancelling no longer leaves a truncated file under the real name",
        "CRITICAL FIX: junction and symlink loops caused infinite recursion and out-of-memory. The tree walk now detects them and the copy completes in under a second",
        "CRITICAL FIX: the file copy loop no longer runs on the UI thread, which caused measurable input lag and reduced throughput on large files",
        "CRITICAL FIX: 30 Hz transfer telemetry was executing on the UI thread for the whole transfer. This was the largest cause of freezes while copying",
        "Progress could exceed 100 percent when a file was skipped. Skipping no longer inflates the byte counter",
        "Copy speed history is no longer read while another thread rewrites it, which threw exceptions during rendering",
        "Every external system utility (defrag, diskpart, wmic, fstrim, smartctl, VSS, uninstallers) now has a timeout. Previously a wedged utility hung the app forever",
        "The Linux TRIM command no longer reports success when it actually failed",
        "The VSS snapshot step is now really cancellable: the Cancel button in the migration wizard previously could not interrupt it",
        "Close a window mid-scan no longer leaves a full-disk scan running in the background or crash on dead controls",
        "Fixed cancellation token races: stopping a benchmark or disk wipe and immediately restarting left the new operation impossible to cancel",
        "Fixed cancellation token source leaks in the transfer, archive, duplicate finder, tree analyzer and migration windows",
        "Archive progress no longer passes a still-mutating object to the UI, and is throttled so large archives do not flood the dispatcher",
        "Added self-tests: --theme-audit, --storage-audit, --copy-test and --download-test, which verify behaviour on real files rather than by inspection",
        "Disk copying is verified end to end: byte-identical results, name collisions resolved, existing files preserved, no leftovers"
    )}

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
if ($iscc -and (Test-Path $issFile)) {
    & $iscc "/DMyAppVersion=$cleanVer" $issFile
    if ($LASTEXITCODE -eq 0) {
        $setupExe = "$distDir\MotionCommander-v$cleanVer-Setup.exe"
        $latestSetup = "$distDir\MotionCommander-Latest-Setup.exe"
        if (Test-Path $setupExe) {
            Copy-Item $setupExe -Destination $latestSetup -Force
            $setupMb = [Math]::Round((Get-Item $setupExe).Length / 1MB, 2)
            Write-Host "Installer created: $setupExe ($setupMb MB)" -ForegroundColor Green
        }
    } else {
        Write-Warning "Inno Setup compilation exited with code $LASTEXITCODE"
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
    setupExeUrl = "https://raw.githubusercontent.com/BlackTecCom2000/MotionCommander/main/dist/MotionCommander-v$cleanVer-Setup.exe"
}

$jsonStr = $versionManifest | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText("$repoRoot\version.json", $jsonStr, [System.Text.Encoding]::UTF8)

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

