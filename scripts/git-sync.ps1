<#
.SYNOPSIS
Safely sync local repository with origin/main, with optional build/test/push.

.PARAMETERS
  -Branch: Target branch to sync. Default: main.
  -DryRun: Show what would be done, but do not perform actions.
  -NoBuild: Skip dotnet build after sync.
  -NoTest: Skip dotnet test after build.
  -NoPush: Skip git push after merging.
  -Stash: If there are local changes, stash them before syncing (default: true).

#>
param(
  [string]$Branch = "main",
  [switch]$DryRun,
  [switch]$NoBuild,
  [switch]$NoTest,
  [switch]$NoPush,
  [switch]$Stash = $true
)

$ErrorActionPreference = 'Stop'

# Dry run guide
if ($DryRun) {
    Write-Host "[DryRun] Planned steps: stash (if dirty, via -Stash), fetch --all, checkout $Branch, pull --rebase origin/$Branch, stash pop (if applied), build (unless -NoBuild), test (unless -NoTest), push (unless -NoPush)." -ForegroundColor Yellow
    exit 0
}

# Ensure we are in repo root
$root = (Get-Location).Path

# Detect if there are local changes
$dirty = (git status --porcelain) -ne $null
$stashApplied = $false
if (git status --porcelain) {
    if ($Stash) {
        Write-Host "Stashing local changes..." -ForegroundColor Cyan
        git stash push -u -m "git-sync stash before sync"
        $stashApplied = $true
    } else {
        Write-Host "Uncommitted changes detected. Use -Stash to stash automatically or commit changes before syncing." -ForegroundColor Red
        exit 1
    }
}

Write-Host "Fetching all remotes..." -ForegroundColor Green
git fetch --all

Write-Host "Checkout to branch $Branch..." -ForegroundColor Green
git checkout $Branch

Write-Host "Pull with rebase from origin/$Branch..." -ForegroundColor Green
git pull --rebase origin $Branch

if ($stashApplied) {
    Write-Host "Applying stash..." -ForegroundColor Green
    git stash pop
}

# Build
if (-not $NoBuild) {
    Write-Host "Building solution..." -ForegroundColor Green
    dotnet build MotionCommander.sln -clp:ErrorsOnly
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Build failed." -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

# Test
if (-not $NoTest) {
    Write-Host "Running tests..." -ForegroundColor Green
    dotnet test MotionCommander.sln
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Tests failed." -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

# Push
if (-not $NoPush) {
    Write-Host "Pushing to origin/$Branch..." -ForegroundColor Green
    git push origin $Branch
}

Write-Host "Sync completed successfully." -ForegroundColor Cyan
