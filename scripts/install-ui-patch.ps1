param(
    # Path to the game folder, e.g. "D:\SteamLibrary\steamapps\common\Survival Log"
    [string]$GameDir = "",

    # Undo the patch (restore the shipped page from the .orig backup)
    [switch]$Restore
)

# Adds a "保存游戏 / Save Game" button + naming dialog to the in-game ESC menu page.
# The page is plain HTML shipped with the game:
#   SurvivalLog_Data\StreamingAssets\WebUI\UI\OutSetting\OutSetting.html
# We only append one <script> block at the end of <body> and keep a .orig backup.
# Re-run this script after every game update (updates overwrite the page).
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$block = Join-Path $root 'ui-patch\save_button.html'

if ([string]::IsNullOrEmpty($GameDir)) { $GameDir = $env:SURVIVALLOG_DIR }
if ([string]::IsNullOrEmpty($GameDir)) {
    throw 'Pass -GameDir "X:\...\Survival Log" or set the SURVIVALLOG_DIR environment variable.'
}

$target = Join-Path $GameDir 'SurvivalLog_Data\StreamingAssets\WebUI\UI\OutSetting\OutSetting.html'
$backup = $target + '.orig'
if (-not (Test-Path -LiteralPath $target)) { throw "menu page not found: $target" }

if ($Restore) {
    if (-not (Test-Path -LiteralPath $backup)) { throw "no backup to restore: $backup" }
    Copy-Item -LiteralPath $backup -Destination $target -Force
    Write-Host "restored original page from $backup"
    return
}

if (-not (Test-Path -LiteralPath $block)) { throw "patch block not found: $block" }

# keep one pristine copy of the shipped page
if (-not (Test-Path -LiteralPath $backup)) {
    Copy-Item -LiteralPath $target -Destination $backup -Force
    Write-Host "backup created -> $backup"
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
$html = [System.IO.File]::ReadAllText($target, [System.Text.Encoding]::UTF8)

# idempotent: drop a previous injection before adding the new one
$stripped = [regex]::Replace(
    $html,
    '(?s)<!-- === SL ManualSave injected block: begin === -->.*?<!-- === SL ManualSave injected block: end === -->\s*',
    '')
if ($stripped -ne $html) {
    $html = $stripped
    Write-Host 'removed previous injection'
}

$snippet = [System.IO.File]::ReadAllText($block, [System.Text.Encoding]::UTF8)
$idx = $html.LastIndexOf('</body>', [System.StringComparison]::OrdinalIgnoreCase)
if ($idx -lt 0) { throw "no </body> found in $target" }

$patched = $html.Substring(0, $idx) + $snippet + "`n" + $html.Substring($idx)
[System.IO.File]::WriteAllText($target, $patched, $utf8)

Write-Host ("patched {0} (+{1} bytes)" -f $target, ($patched.Length - $html.Length))
