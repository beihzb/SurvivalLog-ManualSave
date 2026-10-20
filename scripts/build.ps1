param(
    # Path to the game folder, e.g. "D:\SteamLibrary\steamapps\common\Survival Log"
    [string]$GameDir = "",
    [switch]$NoDeploy
)

# Build SLManualSave.dll and copy it into <game>\BepInEx\plugins.
# Requires: .NET 6 SDK, BepInEx 6 (IL2CPP) already installed and the game started once
#           (so that BepInEx\interop\*.dll exist).
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$csproj = Join-Path $root 'src\SLManualSave\SLManualSave.csproj'

if ([string]::IsNullOrEmpty($GameDir)) { $GameDir = $env:SURVIVALLOG_DIR }
if ([string]::IsNullOrEmpty($GameDir)) {
    throw 'Pass -GameDir "X:\...\Survival Log" or set the SURVIVALLOG_DIR environment variable.'
}
if (-not (Test-Path -LiteralPath $GameDir)) { throw "game folder not found: $GameDir" }
if (-not (Test-Path -LiteralPath (Join-Path $GameDir 'BepInEx\interop\HotUpdate.dll'))) {
    throw "BepInEx interop assemblies missing. Install BepInEx 6 (IL2CPP) and run the game once first."
}

$env:GameDir = $GameDir
Write-Host "building $csproj (GameDir=$GameDir)"
& dotnet build $csproj -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

$dll = Join-Path $root 'src\SLManualSave\bin\Release\SLManualSave.dll'
if (-not (Test-Path -LiteralPath $dll)) { throw "build output not found: $dll" }

if (-not $NoDeploy) {
    $plugins = Join-Path $GameDir 'BepInEx\plugins'
    if (-not (Test-Path -LiteralPath $plugins)) { New-Item -ItemType Directory -Path $plugins -Force | Out-Null }
    Copy-Item -LiteralPath $dll -Destination (Join-Path $plugins 'SLManualSave.dll') -Force
    Write-Host "deployed -> $plugins\SLManualSave.dll"
}

Get-Item -LiteralPath $dll | Select-Object FullName, Length, LastWriteTime | Format-List
