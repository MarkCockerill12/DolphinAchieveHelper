<#
.SYNOPSIS
    Builds a Dolphin that labels its RetroAchievements challenge indicators.

.DESCRIPTION
    Dolphin already shows a badge in the bottom-right corner for every achievement that
    is active where you are standing. It draws the icon and nothing else, so you cannot
    tell what the achievement is. This applies a small patch (three files, ~36 lines)
    that draws the achievement's name and how to unlock it beside the badge.

    Because the indicator is drawn by Dolphin's own renderer, inside the emulated frame,
    it works in every display mode including exclusive fullscreen - which no external
    overlay can do.

    Run this again after a Dolphin update to rebuild against the newer release.

.PARAMETER Tag
    Dolphin release tag to build. Defaults to the release this was tested against.

.PARAMETER WorkDir
    Where to clone and build. Needs roughly 10 GB.

.PARAMETER Install
    Copy the built Dolphin.exe over an existing Dolphin install (the old one is kept
    as Dolphin.exe.stock).

.EXAMPLE
    .\Build-PatchedDolphin.ps1 -Install "C:\Dolphin-x64"
#>
[CmdletBinding()]
param(
    [string]$Tag = '2606a',
    [string]$WorkDir = "$env:USERPROFILE\dolphin-patched-build",
    [string]$Install
)

$ErrorActionPreference = 'Stop'
$patch = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'challenge-details.patch'
if (-not (Test-Path $patch)) { throw "Patch not found: $patch" }

function Ok($m) { Write-Host "  [ok] $m" -ForegroundColor Green }
function Say($m) { Write-Host "  $m" -ForegroundColor Gray }

# --- toolchain ---------------------------------------------------------------
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) {
    throw "Visual Studio Installer not found. Install 'Visual Studio Build Tools 2022' with the 'Desktop development with C++' workload."
}
$vsPath = & $vswhere -products * -latest `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsPath) { throw "No Visual Studio C++ toolset found. Install the 'Desktop development with C++' workload." }
$msbuild = Join-Path $vsPath 'MSBuild\Current\Bin\MSBuild.exe'
if (-not (Test-Path $msbuild)) { throw "MSBuild not found under $vsPath" }
Ok "MSBuild: $msbuild"

# --- source ------------------------------------------------------------------
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
$src = Join-Path $WorkDir 'dolphin'

if (-not (Test-Path (Join-Path $src '.git'))) {
    Say "Cloning Dolphin $Tag (this is a few GB)..."
    & git clone --branch $Tag --depth 1 --recurse-submodules --shallow-submodules --jobs 8 `
        https://github.com/dolphin-emu/dolphin.git $src
    if ($LASTEXITCODE -ne 0) { throw "git clone failed" }
} else {
    Say "Reusing existing clone; resetting to a clean $Tag tree..."
    & git -C $src reset --hard | Out-Null
    & git -C $src clean -fd | Out-Null
}
Ok "Source ready: $src"

Say "Applying challenge-details patch..."
& git -C $src apply --3way $patch
if ($LASTEXITCODE -ne 0) {
    throw "Patch did not apply. Dolphin's achievement UI probably changed in $Tag; the patch needs updating."
}
Ok "Patch applied"

# --- build -------------------------------------------------------------------
Say "Building (30-60 minutes on a laptop)..."
& $msbuild (Join-Path $src 'Source\dolphin-emu.sln') `
    -p:Configuration=Release -p:Platform=x64 -m -v:minimal
if ($LASTEXITCODE -ne 0) { throw "Build failed. See the output above." }

$exe = Join-Path $src 'Binary\x64\Dolphin.exe'
if (-not (Test-Path $exe)) { throw "Build reported success but $exe is missing." }
Ok "Built $exe"

# --- install -----------------------------------------------------------------
if ($Install) {
    $target = Join-Path $Install 'Dolphin.exe'
    if (-not (Test-Path $target)) { throw "No Dolphin.exe in $Install" }
    Get-Process -Name Dolphin -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    if (-not (Test-Path "$target.stock")) { Copy-Item $target "$target.stock" }
    Copy-Item $exe $target -Force
    Ok "Installed into $Install (original kept as Dolphin.exe.stock)"
    Say "Dolphin's auto-updater will overwrite this on the next update; re-run to re-apply."
}

Write-Host ""
Write-Host "  Done." -ForegroundColor Green
