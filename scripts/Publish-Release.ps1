param(
    [string]$Version = "3.8.20",
    [string[]]$Notes = $null,
    [switch]$SkipBuild,
    [switch]$SkipPush
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

if ($null -eq $Notes -or $Notes.Count -eq 0) {
    $Notes = @(
        "Fixed: the Storage tab no longer freezes. Disk discovery ran the defrag analyser synchronously on the UI thread, and defrag never returns when launched from a GUI process, so the tab hung permanently",
        "Fixed: a disk with no S.M.A.R.T. and no temperature sensor no longer shows A+ and Excellent. Free space was weighted like a health signal, so an unmeasured disk scored 100/100 purely for having empty space",
        "The Storage Score now states which indicators were actually measured and how confident the verdict is, and issues no grade at all when nothing was measured",
        "Disk cards show n/d instead of a fabricated 0 degrees C and a letter grade that was never calculated",
        "Fixed: Pause in the download manager did not pause. The engine instance was dropped immediately, so the cancellation token could never be cancelled and the transfer kept running at full speed",
        "Fixed: Delete in the download manager did not stop the transfer, so the file kept being written after the row disappeared",
        "Fixed: Resume could start a second engine on the same file, interleaving segments and corrupting the download",
        "Download manager validates the address and reports the reason instead of silently doing nothing on invalid input; file names are sanitised and cannot escape the download folder",
        "Download manager is readable in light themes: the URL box was white on white and the progress bars were invisible",
        "Per-segment progress bars were always empty because the custom ProgressBar template had an indicator with no width",
        "OS Cloning no longer reports success while copying nothing. Target preparation, data copy, bootloader and verification now call the already-implemented service and fail loudly",
        "Rollback after a failed migration now clears temporary partition letters and reports anything it could not undo, instead of claiming a clean rollback",
        "Fixed 45 design tokens that were silently missing: the theme dictionaries declared assembly=mscorlib, which does not exist in .NET, so every sys:Double in them failed to load. This affected tooltip size, scrollbar geometry and control heights",
        "Fixed 10 resource keys that were referenced but never defined: the main window lost its rounded corners, the migration wizard rendered black on black, and ComboBox hit targets collapsed to zero",
        "Light themes no longer render dark cards: 30 heavily used surface, border and media tokens were never repainted by Apply() and stayed dark navy",
        "Theme change now repaints correctly. PropertyChanged was raised before Apply(), so handlers drew with the previous theme's brushes",
        "Accent, window backdrop and animation quality are now actually restored after a restart; they were written to settings and never read back",
        "Removed a duplicate settings model that overwrote the same settings.json with a disjoint schema, silently resetting scroll and visual settings",
        "Theme selection is resolved by name, so adding a theme no longer makes saved selections resolve to a neighbouring theme",
        "New themes: Cosmic Nebula, Deep Sea, Terminal Amber and Rose Quartz (12 -> 16), with per-theme window radius, status palette and typography",
        "New: animation quality switch Economy / Maximum. Economy halves the frame rate and disables the live backdrop; the Windows accessibility setting for animations is now respected",
        "New: install mode switch Full install / Portable / Auto, plus a --portable switch. Mutable state (staging, crash log, benchmarks) moved to the user profile, so autoupdate and crash logging work under Program Files instead of failing with access denied",
        "Animation performance: removed roughly 250 allocations and 250 Freeze() calls per frame from the transfer visualiser, cached FormattedText, and stopped every animated control from rendering in a minimised window or on a hidden tab",
        "The live starfield background now appears only in themes that declare it, instead of behind the light themes as well",
        "The throughput graph timer no longer runs on tabs that do not show it",
        "Fixed: process wait timeouts that were ignored, leaving tasks blocked forever on Result after a failed wait, and leaving cancelled defrag processes running in the background",
        "Fixed: MSFT_Partition reports DriveLetter as NUL for partitions without a letter, which made the app treat them as having a drive letter and operate on a non-existent path",
        "Fragmentation measurement moved off the discovery path into a bounded background task, and now reports No data instead of blocking the tab",
        "Removed 11 empty event handlers left over from the settings panel's move to its own window"
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

