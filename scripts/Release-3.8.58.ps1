$ErrorActionPreference = "Stop"

$notes = @(
    "Interface fully migrated to Liquid Glass design: App.xaml fallback brushes aligned to MotionGlass palette (dark background #050912, glass card, primary text #F8FAFF, cyan accent #00D4FF) - no light flash on first frame, accent matches the rest of the glass palette",
    "All structural colors (background, cards, text, borders, accent, navigation, inputs, scrollbars, context menus) resolved through ThemeManager.Apply() into liquid LiquidGlass*/GlassBorder* tokens",
    "Status colors kept semantic (green success / red danger / amber warning)",
    "TAJ ARENA branding left untouched",
    "Verified: build 0 errors / 0 warnings, 23/23 screens (RESULT: OK)"
)

& "$PSScriptRoot\Publish-Release.ps1" -Version 3.8.58 -Notes $notes
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
