$ErrorActionPreference = "Stop"

$notes = @(
    "Theme system simplified to exactly two modes: Day (light) and Night (dark) - the previous 16 themes are removed",
    "Real liquid glass: both modes use DWM Acrylic backdrop with genuinely translucent surface brushes, so the desktop blur shows through the UI instead of flat panels",
    "Old saved theme names migrate automatically (light themes become Day, everything else becomes Night)",
    "Backdrop override option removed - the window always uses the theme's Acrylic material, fixing stale MicaAlt overrides that disabled the blur",
    "Day/Night radio toggle in Settings replaces the theme dropdown; typography unified to Segoe UI Variable",
    "Win10 fallback flattens translucent layers to solid colors when DWM Acrylic is unavailable",
    "Verified: build 0 errors / 0 warnings, 23/23 screens OK, update tests 20/20, game tuning 18/18, DWM backdrop = Acrylic (3)"
)

& "$PSScriptRoot\Publish-Release.ps1" -Version 3.8.59 -Notes $notes
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
