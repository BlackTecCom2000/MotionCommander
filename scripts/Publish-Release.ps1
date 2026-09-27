param(
    [string]$Version = "3.8.19",
    [string[]]$Notes = $null,
    [switch]$SkipBuild,
    [switch]$SkipPush
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

if ($null -eq $Notes -or $Notes.Count -eq 0) {
    $Notes = @(
        "Honest storage diagnostics: disk health, temperature, wear, power-on hours, TRIM state and S.M.A.R.T. attributes are now read from the controller instead of being invented",
        "Removed the per-media-type placeholder table that reported 41 C / 1840 hours / A+ for any disk whose controller could not be queried",
        "S.M.A.R.T. table is now parsed from real MSStorageDriver_FailurePredictData with real thresholds, so failing attributes (Reallocated, Pending, Uncorrectable) can finally be reported",
        "Disk type is determined from MSFT_PhysicalDisk spindle speed and bus type instead of guessing from the drive letter; drives with 'PRO' in the model name are no longer misreported as NVMe",
        "Health score is averaged only over parameters that were actually measured, and shows 'n/a' when no data is available instead of always grading A+",
        "Fragmentation is measured with the real defrag analyzer; TRIM state is queried via fsutil; both were previously hardcoded constants",
        "Benchmark IOPS is computed correctly and single-threaded tests are no longer labelled Q8T1/Q32T1",
        "Storage reports (txt/CSV/JSON) mark which values are measured and which are unavailable",
        "Added scripts/Build-CrossPlatform.ps1 for cross-platform CLI packages and GitHub Actions diagnostics",
        "Fixed pipe deadlock in Drive Optimizer and S.M.A.R.T. scan",
        "Fixed blocking Dispatcher.Invoke that throttled file copy throughput",
        "Fixed Cancel button in WizTree and Duplicate Finder",
        "Fixed directory junction recursion in Duplicate Finder",
        "Fixed shared DependencyProperty collection that blanked the drive tree",
        "Settings now persist across restarts",
        "Security: full payment card numbers removed from source, binaries and public repository (PCI-DSS)"
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

