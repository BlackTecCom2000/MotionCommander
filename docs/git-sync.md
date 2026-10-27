Git Sync Helper

A reproducible way to sync your project with the origin/main, optionally building and testing, and pushing. These helpers come in two forms:

- scripts/git-sync.ps1 (PowerShell, Windows-friendly)
- scripts/git-sync.sh (bash, cross-platform)

Usage:

PowerShell (Windows):
- Run: pwsh -File scripts/git-sync.ps1
- Or use with options: pwsh -File scripts/git-sync.ps1 -Branch main -DryRun

Bash (Unix-like):
- Run: bash scripts/git-sync.sh main
- Options: set DRY_RUN=1 to dry-run, NO_BUILD=1, NO_TEST=1, NO_PUSH=1, SHOULD_STASH=0

Notes:
- By default, if there are local changes, the script stashes them before syncing, then applies the stash after pulling.
- It builds and tests unless you disable with flags.
- It pushes to the current branch unless you disable.

