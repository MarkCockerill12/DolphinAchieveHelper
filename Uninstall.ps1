<#
.SYNOPSIS
    Removes Dolphin Achiever and restores Dolphin's logging settings.

.PARAMETER KeepCache
    Keep the downloaded achievement data and badge images.
#>
[CmdletBinding()]
param([switch]$KeepCache)

$ErrorActionPreference = 'Stop'
function Ok($m) { Write-Host "  [ok] $m" -ForegroundColor Green }
function Warn($m) { Write-Host "  [!]  $m" -ForegroundColor Yellow }

Write-Host ""
Write-Host "  Dolphin Achiever - uninstaller" -ForegroundColor Cyan

Get-Process -Name DolphinAchiever -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

$installDir = Join-Path $env:LOCALAPPDATA 'DolphinAchiever'
$cfgPath = Join-Path $installDir 'install.json'

# Put Logger.ini back the way it was.
if (Test-Path $cfgPath) {
    try {
        $cfg = Get-Content -LiteralPath $cfgPath -Raw | ConvertFrom-Json
        foreach ($name in @('Logger.ini', 'GFX.ini')) {
            $ini = Join-Path $cfg.UserDir "Config\$name"
            $backup = "$ini.achiever-backup"
            if (Test-Path $backup) {
                Copy-Item $backup $ini -Force
                Remove-Item $backup -Force
                Ok "Restored $name"
            }
        }
    } catch { Warn "Could not restore Logger.ini: $($_.Exception.Message)" }
}

foreach ($lnk in @(
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Dolphin (Achievements).lnk'),
    (Join-Path ([Environment]::GetFolderPath('Programs')) 'Dolphin (Achievements).lnk'))) {
    if (Test-Path $lnk) { Remove-Item $lnk -Force; Ok "Removed $lnk" }
}

if (Test-Path $installDir) {
    if ($KeepCache) {
        Remove-Item (Join-Path $installDir 'bin') -Recurse -Force -ErrorAction SilentlyContinue
        Ok "Removed program files (cache kept at $installDir\cache)"
    } else {
        Remove-Item $installDir -Recurse -Force
        Ok "Removed $installDir"
    }
}

Write-Host "  Done. Dolphin itself was never modified." -ForegroundColor Green
Write-Host ""
