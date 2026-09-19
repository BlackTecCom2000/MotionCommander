param(
    [string]$Version = "3.8.3",
    [string[]]$Notes = $null,
    [switch]$SkipBuild,
    [switch]$SkipPush
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

if ($null -eq $Notes -or $Notes.Count -eq 0) {
    $Notes = @(
        "Direct I/O Streamer: Hardware saturation PCIe/SATA pipeline with null-allocation pool",
        "Speed Telemetry HUD: 30 FPS Canvas speed graph, 4-tile neon indicators and bottleneck detector",
        "WizTree Disk Space Analyzer: Folder weights treemap, Top-100 heavy files and extensions statistics",
        "Duplicate File Cleaner: 3-stage high-speed scan (Size -> 4KB Pre-Hash -> Full SHA-256)",
        "System Driver Inspector: WMI PnP device audit, state indicators, devmgmt.msc and report export",
        "Settings Window: 10 designer themes (Cyberpunk, OLED Midnight, Matrix, Sunset), backdrops, haptics and JSON editor"
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
