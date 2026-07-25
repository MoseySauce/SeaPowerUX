<#
.SYNOPSIS
    Installs a built SeaPowerUX release into a Sea Power directory. For players, on Windows.

.DESCRIPTION
    This is NOT the contributor build script — it installs already-built DLLs from a release
    bundle. To build from source, use scripts/deploy-local.sh.

    It copies the bundled BepInEx\ tree (plugins + the default layout) over the game's own
    BepInEx\ folder. BepInEx 5 (x64) must already be installed and have been run once. See
    INSTALL.md.

.PARAMETER GameDir
    The folder containing "Sea Power.exe". Auto-detected from Steam if omitted.

.PARAMETER Uninstall
    Remove the pack. Leaves BepInEx and your saved settings alone.

.PARAMETER Source
    Bundle directory containing BepInEx\. Defaults to beside this script.

.PARAMETER Force
    Install even if the game appears to be running (not recommended).

.EXAMPLE
    .\install.ps1

.EXAMPLE
    .\install.ps1 -GameDir "D:\Steam\steamapps\common\Sea Power"

.EXAMPLE
    .\install.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [string]$GameDir,
    [switch]$Uninstall,
    [string]$Source,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

# The pack's own folders under BepInEx\plugins. Used to verify an install and to know exactly what
# to remove on uninstall — nothing else in plugins\ is touched.
$Plugins = @(
    'SeaPowerUX.Core'
    'SeaPowerUX.TacticalPiP'
    'SeaPowerUX.FlightDeck'
    'SeaPowerUX.WindowMemory'
    'SeaPowerUX.FormationManager'
)
$DefaultsFile = 'com.moseysauce.seapowerux.defaults.cfg'

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

function Fail($msg) { Write-Host "ERROR: $msg" -ForegroundColor Red; exit 1 }

# --- Locate the bundle (BepInEx\ payload) --------------------------------------------------------
function Find-Source {
    if ($Source) { return $Source }
    if ($env:SEA_POWER_SOURCE) { return $env:SEA_POWER_SOURCE }
    foreach ($c in @($ScriptDir, (Split-Path -Parent $ScriptDir))) {
        if (Test-Path (Join-Path $c 'BepInEx\plugins')) { return $c }
    }
    return $null
}

# --- Locate the game -----------------------------------------------------------------------------
# Reads Steam's libraryfolders.vdf to find installs on non-default drives, then falls back to the
# default library path.
function Get-SteamLibraries {
    $libraries = @()
    $steamRoot = $null
    try {
        $steamRoot = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction Stop).SteamPath
    } catch { }
    if (-not $steamRoot) { $steamRoot = 'C:\Program Files (x86)\Steam' }
    $steamRoot = $steamRoot -replace '/', '\'
    $libraries += $steamRoot

    $vdf = Join-Path $steamRoot 'steamapps\libraryfolders.vdf'
    if (Test-Path $vdf) {
        foreach ($line in Get-Content $vdf) {
            if ($line -match '"path"\s+"([^"]+)"') {
                $libraries += ($Matches[1] -replace '\\\\', '\')
            }
        }
    }
    return $libraries | Select-Object -Unique
}

function Find-GameDir {
    if ($GameDir) { return $GameDir }
    if ($env:SEA_POWER_GAME_DIR) { return $env:SEA_POWER_GAME_DIR }
    foreach ($lib in Get-SteamLibraries) {
        $candidate = Join-Path $lib 'steamapps\common\Sea Power'
        if (Test-Path $candidate) { return $candidate }
    }
    return $null
}

$game = Find-GameDir
if (-not $game) {
    Fail @"
Could not find a Sea Power installation.

Pass the path explicitly:
    .\install.ps1 -GameDir "D:\Steam\steamapps\common\Sea Power"

That's the folder containing "Sea Power.exe".
"@
}
if (-not (Test-Path $game -PathType Container)) { Fail "Not a directory: $game" }

# Hot-swapping a DLL under the running process corrupts assembly metadata reads and crashes the
# game in unrelated-looking ways. Refuse unless forced.
if (Get-Process -Name 'Sea Power' -ErrorAction SilentlyContinue) {
    if (-not $Force) { Fail "Sea Power is running. Close it first (or pass -Force)." }
}

# --- Uninstall -----------------------------------------------------------------------------------
if ($Uninstall) {
    Write-Host "==> Uninstalling from: $game"
    $removed = $false
    foreach ($p in $Plugins) {
        $dir = Join-Path $game "BepInEx\plugins\$p"
        if (Test-Path $dir) {
            Remove-Item $dir -Recurse -Force
            Write-Host "    removed  plugins\$p"
            $removed = $true
        }
    }
    $cfg = Join-Path $game "BepInEx\config\$DefaultsFile"
    if (Test-Path $cfg) {
        Remove-Item $cfg -Force
        Write-Host "    removed  config\$DefaultsFile"
        $removed = $true
    }
    # Per-plugin user config (com.moseysauce.seapowerux.*.cfg) is left in place on purpose, so a
    # reinstall keeps your window layout and settings.
    if (-not $removed) { Write-Host "    nothing to remove — SeaPowerUX was not installed here." }
    Write-Host "==> Done."
    exit 0
}

# --- Install -------------------------------------------------------------------------------------
$src = Find-Source
if (-not $src) {
    Fail "Could not find the bundled BepInEx\ payload next to this script. Set -Source to the folder containing BepInEx\plugins."
}
if (-not (Test-Path (Join-Path $game 'BepInEx'))) {
    Fail @"
BepInEx is not installed in:
    $game

Install BepInEx 5 (x64) first and launch the game once so it creates its folders, then re-run
this installer. See INSTALL.md.
"@
}

Write-Host "==> Installing SeaPowerUX into: $game"
Write-Host "    from: $src"

# Copy each known item explicitly rather than relying on Copy-Item's -Recurse merge into an
# existing folder, whose behaviour with same-named subfolders is inconsistent across PowerShell
# versions. Each plugin folder is replaced whole so a version swap can't leave stale files behind;
# BepInEx's own files and other mods are never touched.
$destPlugins = Join-Path $game 'BepInEx\plugins'
$destConfig  = Join-Path $game 'BepInEx\config'
New-Item -ItemType Directory -Force -Path $destPlugins, $destConfig | Out-Null

foreach ($p in $Plugins) {
    $from = Join-Path $src "BepInEx\plugins\$p"
    if (-not (Test-Path $from)) { Fail "Bundle is missing plugins\$p — the download may be incomplete." }
    $to = Join-Path $destPlugins $p
    if (Test-Path $to) { Remove-Item $to -Recurse -Force }
    Copy-Item $from $destPlugins -Recurse -Force
}

$defaults = Join-Path $src "BepInEx\config\$DefaultsFile"
if (Test-Path $defaults) { Copy-Item $defaults $destConfig -Force }

Write-Host "==> Verifying..."
$missing = $false
foreach ($p in $Plugins) {
    $dll = Join-Path $game "BepInEx\plugins\$p\$p.dll"
    if (Test-Path $dll) {
        Write-Host "    ok  $p"
    } else {
        Write-Host "    MISSING  $p" -ForegroundColor Red
        $missing = $true
    }
}
if ($missing) { Fail "install incomplete." }

Write-Host "==> Done. Start Sea Power. If anything is missing, check BepInEx\LogOutput.log."
