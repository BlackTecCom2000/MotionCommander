# Installer for Motion Commander
# Author: BlackTecCom - Jaborov Daler (MIT License)

$ErrorActionPreference = "Stop"

$installDir = "$env:LOCALAPPDATA\Programs\MotionCommander"
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "   Installing Motion Commander v3.0 (BlackTecCom)        " -ForegroundColor Yellow
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Create install dir
if (!(Test-Path $installDir)) {
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
}

# 2. Copy binaries
$repoRoot = Split-Path -Parent $PSScriptRoot
$sourceDir = "$repoRoot\Win11CopyDialog\bin\Release\net8.0-windows"

if (!(Test-Path "$sourceDir\Win11CopyDialog.exe")) {
    Write-Host "Publishing binaries..." -ForegroundColor Cyan
    & dotnet publish "$repoRoot\Win11CopyDialog\Win11CopyDialog.csproj" -c Release -o $installDir
    & dotnet publish "$repoRoot\src\MotionCommander.Cli\MotionCommander.Cli.csproj" -c Release -o $installDir
} else {
    Write-Host "Copying files to $installDir..." -ForegroundColor Cyan
    Copy-Item "$sourceDir\*" -Destination $installDir -Recurse -Force
    $cliDir = "$repoRoot\src\MotionCommander.Cli\bin\Release\net8.0"
    if (Test-Path "$cliDir\motion.exe") {
        Copy-Item "$cliDir\motion.exe" -Destination $installDir -Force
    }
}

$exePath = "$installDir\Win11CopyDialog.exe"

# 3. Create Shortcuts
$wshell = New-Object -ComObject WScript.Shell

# Desktop shortcuts
$desktopDirs = @([Environment]::GetFolderPath('Desktop'))
$oneDriveDesktop = "$env:USERPROFILE\OneDrive\Desktop"
if (Test-Path $oneDriveDesktop) { $desktopDirs += $oneDriveDesktop }

foreach ($d in $desktopDirs) {
    if (Test-Path $d) {
        $shortcut = $wshell.CreateShortcut("$d\Motion Commander.lnk")
        $shortcut.TargetPath = $exePath
        $shortcut.WorkingDirectory = $installDir
        $shortcut.Description = "Motion Commander - Storage Control Center and File Manager"
        $shortcut.IconLocation = "$exePath,0"
        $shortcut.Save()
    }
}

# Start menu shortcut
$startMenuDir = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Motion Commander"
if (!(Test-Path $startMenuDir)) { New-Item -ItemType Directory -Path $startMenuDir -Force | Out-Null }
$startShortcut = $wshell.CreateShortcut("$startMenuDir\Motion Commander.lnk")
$startShortcut.TargetPath = $exePath
$startShortcut.WorkingDirectory = $installDir
$startShortcut.Description = "Motion Commander - Storage Control Center and File Manager"
$startShortcut.IconLocation = "$exePath,0"
$startShortcut.Save()

# 4. Windows registry registration
$regPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MotionCommander"
if (!(Test-Path $regPath)) { New-Item -Path $regPath -Force | Out-Null }
Set-ItemProperty -Path $regPath -Name "DisplayName" -Value "Motion Commander"
Set-ItemProperty -Path $regPath -Name "DisplayVersion" -Value "3.0.0"
Set-ItemProperty -Path $regPath -Name "Publisher" -Value "BlackTecCom - Jaborov Daler"
Set-ItemProperty -Path $regPath -Name "InstallLocation" -Value $installDir
Set-ItemProperty -Path $regPath -Name "DisplayIcon" -Value "$exePath,0"
Set-ItemProperty -Path $regPath -Name "UninstallString" -Value "powershell -Command Remove-Item '$installDir' -Recurse -Force; Remove-Item '$regPath' -Force"

# 5. User PATH for 'motion' command
$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
if ($userPath -notlike "*$installDir*") {
    [Environment]::SetEnvironmentVariable("Path", "$userPath;$installDir", "User")
}

Write-Host ""
Write-Host "SUCCESS: Motion Commander is installed on your PC!" -ForegroundColor Green
Write-Host "  Location:   $installDir" -ForegroundColor White
Write-Host "  Shortcuts:  Desktop and Start Menu" -ForegroundColor White
Write-Host "  CLI:        'motion' command available in any terminal" -ForegroundColor White
Write-Host "==========================================================" -ForegroundColor Cyan
