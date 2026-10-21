$ErrorActionPreference = "Stop"

$notes = @(
    "Added a real Games tab (index 6, --tab-games): measured hardware, per-game config presets and 11 reversible system tweaks",
    "FPS is measured through ETW DXGI/D3D9 Present events; without administrator rights the tool refuses to print numbers instead of inventing them",
    "A difference below 3% is reported as within measurement noise, never as a gain",
    "Every tweak snapshots the original registry value (existence, type, data), and 'Revert all' restores exactly those originals",
    "Game config presets edit only keys that already exist, keep a timestamped backup next to the file and verify the result by re-reading it; a missing file is reported rather than created",
    "Correction: optimisation profiles now switch only the Windows power scheme and verify it after the change; unimplemented claims about core parking, SSD TRIM/SLC flush, I/O buffers, fan behaviour and 6-12 C temperature drops were removed from the profiles and the UI",
    "Diagnostics: 18 game-tuning tests pass, 23 of 23 screens pass the view audit, solution builds with 0 errors and 0 warnings"
)

& "$PSScriptRoot\Publish-Release.ps1" -Version 3.8.55 -SkipPush -Notes $notes
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
