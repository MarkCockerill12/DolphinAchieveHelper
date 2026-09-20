<#
.SYNOPSIS
    Installs Dolphin Achiever: an achievement-location overlay for Dolphin + RetroAchievements.

.DESCRIPTION
    Finds Dolphin, compiles the overlay with the C# compiler that ships with Windows
    (no SDK, no runtime download), turns on the one Dolphin log channel the overlay
    needs, and creates a "Dolphin (Achievements)" shortcut.

    Nothing is written into the Dolphin program folder, so Dolphin updates never
    overwrite any of it. The only Dolphin-side change is in User\Config\Logger.ini,
    which lives in the user data folder and also survives updates.

.PARAMETER DolphinPath
    Path to Dolphin.exe. Auto-detected when omitted.

.PARAMETER Corner
    Screen corner for popups: 0=top-left 1=top-right 2=bottom-left 3=bottom-right.

.PARAMETER Seconds
    How long each popup stays on screen.

.PARAMETER NoShortcut
    Skip creating shortcuts.

.EXAMPLE
    .\Install.ps1
.EXAMPLE
    .\Install.ps1 -DolphinPath "D:\Emu\Dolphin-x64\Dolphin.exe" -Corner 1 -Seconds 12
#>
[CmdletBinding()]
param(
    [string]$DolphinPath,
    [ValidateRange(0, 3)][int]$Corner = 3,
    [ValidateRange(2, 60)][double]$Seconds = 7,
    [ValidateSet('hover','always','never')][string]$Text = 'hover',
    [switch]$NoFullscreenFix,
    [switch]$NoShortcut
)

$ErrorActionPreference = 'Stop'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

function Say($msg, $color = 'Gray') { Write-Host "  $msg" -ForegroundColor $color }
function Ok($msg) { Write-Host "  [ok] $msg" -ForegroundColor Green }
function Warn($msg) { Write-Host "  [!]  $msg" -ForegroundColor Yellow }

Write-Host ""
Write-Host "  Dolphin Achiever - installer" -ForegroundColor Cyan
Write-Host "  ----------------------------" -ForegroundColor Cyan

# ---------------------------------------------------------------- find Dolphin
function Find-Dolphin {
    param([string]$Explicit)

    if ($Explicit) {
        if (Test-Path -LiteralPath $Explicit) { return (Resolve-Path -LiteralPath $Explicit).Path }
        throw "Dolphin.exe not found at: $Explicit"
    }

    # 1. A running instance is the most reliable answer.
    $proc = Get-Process -Name Dolphin, DolphinQt -ErrorAction SilentlyContinue |
            Where-Object { $_.Path } | Select-Object -First 1
    if ($proc) { return $proc.Path }

    # 2. Common install locations.
    $roots = @(
        $env:LOCALAPPDATA, $env:ProgramFiles, ${env:ProgramFiles(x86)},
        'C:\', $env:USERPROFILE,
        (Join-Path $env:USERPROFILE 'Desktop'),
        (Join-Path $env:USERPROFILE 'Downloads')
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }

    foreach ($r in $roots) {
        foreach ($n in @('Dolphin-x64\Dolphin.exe', 'Dolphin\Dolphin.exe',
                         'Dolphin Emulator\Dolphin.exe', 'Dolphin.exe')) {
            $p = Join-Path $r $n
            if (Test-Path -LiteralPath $p) { return (Resolve-Path -LiteralPath $p).Path }
        }
    }

    # 3. Uninstall registry entries.
    foreach ($hive in @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*')) {
        $hit = Get-ItemProperty $hive -ErrorAction SilentlyContinue |
               Where-Object { $_.DisplayName -like '*Dolphin*' -and $_.InstallLocation }
        foreach ($h in $hit) {
            $p = Join-Path $h.InstallLocation 'Dolphin.exe'
            if (Test-Path -LiteralPath $p) { return (Resolve-Path -LiteralPath $p).Path }
        }
    }

    # 4. Last resort: a shallow scan of fixed drives.
    foreach ($d in (Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Free -ne $null })) {
        $hit = Get-ChildItem -LiteralPath $d.Root -Filter 'Dolphin.exe' -Recurse -Depth 3 `
                             -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    throw "Could not find Dolphin.exe. Re-run with -DolphinPath 'C:\path\to\Dolphin.exe'."
}

$dolphinExe = Find-Dolphin -Explicit $DolphinPath
$dolphinDir = Split-Path -Parent $dolphinExe
Ok "Dolphin: $dolphinExe"

# Portable installs keep User next to the exe; otherwise it is in Documents.
$userDir = Join-Path $dolphinDir 'User'
if (-not ((Test-Path (Join-Path $dolphinDir 'portable.txt')) -and (Test-Path $userDir))) {
    $docs = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Dolphin Emulator'
    if (Test-Path $docs) { $userDir = $docs }
}
Ok "Dolphin user data: $userDir"

# ------------------------------------------------------------------- compiler
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) {
    throw "The .NET Framework C# compiler was not found. It ships with Windows 10/11; is .NET Framework 4.x enabled?"
}

# --------------------------------------------------------------------- build
$installDir = Join-Path $env:LOCALAPPDATA 'DolphinAchiever'
$binDir = Join-Path $installDir 'bin'
New-Item -ItemType Directory -Force -Path $binDir | Out-Null

$srcDir = Join-Path $ScriptDir 'src'
if (-not (Test-Path $srcDir)) { throw "Source folder not found: $srcDir" }
$sources = Get-ChildItem -LiteralPath $srcDir -Filter '*.cs' | ForEach-Object { $_.FullName }
if ($sources.Count -eq 0) { throw "No .cs files in $srcDir" }

$exe = Join-Path $binDir 'DolphinAchiever.exe'

# The overlay may be running from a previous install.
Get-Process -Name DolphinAchiever -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

Say "Compiling $($sources.Count) source files..."
$cscArgs = @(
    '-nologo', '-platform:x64', '-target:winexe', '-optimize+',
    "-out:$exe",
    '-r:System.dll', '-r:System.Drawing.dll', '-r:System.Windows.Forms.dll',
    '-r:System.Web.Extensions.dll', '-r:System.Core.dll'
) + $sources

$out = & $csc @cscArgs 2>&1
if ($LASTEXITCODE -ne 0) {
    $out | ForEach-Object { Write-Host $_ -ForegroundColor Red }
    throw "Compilation failed."
}
Ok "Built $exe"

# ------------------------------------------------- enable the RA log channel
# The overlay reads the game ID out of Dolphin's log, because Dolphin has already
# hashed the disc and asked the RetroAchievements server. Doing that independently
# would mean reimplementing RetroAchievements' disc hashing, including RVZ/WIA
# decompression, for no benefit.
function Set-IniValue([System.Collections.Generic.List[string]]$L, [string]$Section, [string]$Key, [string]$Value) {
    $inSec = $false; $done = $false; $lastIdx = -1
    for ($i = 0; $i -lt $L.Count; $i++) {
        $t = $L[$i].Trim()
        if ($t -match '^\[(.+)\]$') {
            if ($inSec -and -not $done) { $L.Insert($lastIdx + 1, "$Key = $Value"); return $true }
            $inSec = ($Matches[1] -eq $Section)
            continue
        }
        if ($inSec) {
            $lastIdx = $i
            if ($t -match "^\s*$([regex]::Escape($Key))\s*=") { $L[$i] = "$Key = $Value"; $done = $true }
        }
    }
    if ($inSec -and -not $done) { $L.Add("$Key = $Value"); return $true }
    if (-not $done) {
        # Section is absent entirely.
        $L.Add("[$Section]"); $L.Add("$Key = $Value"); return $true
    }
    return $done
}

function Update-Ini([string]$Path, [hashtable[]]$Settings) {
    if (-not (Test-Path $Path)) { return $false }
    $backup = "$Path.achiever-backup"
    if (-not (Test-Path $backup)) { Copy-Item $Path $backup }
    $lines = [System.Collections.Generic.List[string]](Get-Content -LiteralPath $Path)
    foreach ($s in $Settings) { Set-IniValue $lines $s.Section $s.Key $s.Value | Out-Null }
    Set-Content -LiteralPath $Path -Value $lines -Encoding ASCII
    return $true
}

$loggerIni = Join-Path $userDir 'Config\Logger.ini'
if (Update-Ini $loggerIni @(
        @{ Section = 'Logs';    Key = 'RetroAchievements'; Value = 'True' },
        @{ Section = 'Options'; Key = 'WriteToFile';       Value = 'True' },
        @{ Section = 'Options'; Key = 'Verbosity';         Value = '4' })) {
    Ok "Enabled Dolphin's RetroAchievements log channel (backup: Logger.ini.achiever-backup)"
} else {
    Warn "Logger.ini not found. Start Dolphin once, then re-run this installer."
}

# ------------------------------------------------- make fullscreen work
# Dolphin's default Direct3D backend takes *exclusive* fullscreen, which draws
# straight to the display and hides every other window, including this overlay.
# Borderless fullscreen looks and performs the same but composites normally.
if (-not $NoFullscreenFix) {
    $gfxIni = Join-Path $userDir 'Config\GFX.ini'
    if (Update-Ini $gfxIni @(@{ Section = 'Settings'; Key = 'BorderlessFullscreen'; Value = 'True' })) {
        Ok "Set Dolphin to borderless fullscreen so the overlay is visible in-game"
    } else {
        Warn "GFX.ini not found; if the overlay is invisible in fullscreen, turn on"
        Warn "Graphics > Advanced > Borderless Fullscreen in Dolphin."
    }
}

# --------------------------------------------------- check RA is set up
$raIni = Join-Path $userDir 'Config\RetroAchievements.ini'
if (Test-Path $raIni) {
    $raText = Get-Content -LiteralPath $raIni -Raw
    if ($raText -match '(?m)^\s*ApiToken\s*=\s*\S') { Ok "RetroAchievements account is signed in." }
    else { Warn "Dolphin is not signed in to RetroAchievements. Sign in via Tools > Achievements." }
} else {
    Warn "No RetroAchievements config yet. Enable achievements in Dolphin, then re-run."
}

# ------------------------------------------------------------------ shortcuts
if (-not $NoShortcut) {
    $args = "--launch --dolphin `"$dolphinExe`" --corner $Corner --seconds $Seconds --text $Text"
    $ws = New-Object -ComObject WScript.Shell
    $targets = @(
        (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Dolphin (Achievements).lnk'),
        (Join-Path ([Environment]::GetFolderPath('Programs')) 'Dolphin (Achievements).lnk')
    )
    foreach ($lnkPath in $targets) {
        try {
            $lnk = $ws.CreateShortcut($lnkPath)
            $lnk.TargetPath = $exe
            $lnk.Arguments = $args
            $lnk.WorkingDirectory = $binDir
            $lnk.IconLocation = "$dolphinExe,0"
            $lnk.Description = 'Dolphin with RetroAchievements location popups'
            $lnk.Save()
            Ok "Shortcut: $lnkPath"
        } catch { Warn "Could not create $lnkPath : $($_.Exception.Message)" }
    }
}

# --------------------------------------------------------------- save config
@{
    DolphinExe = $dolphinExe
    UserDir    = $userDir
    Corner     = $Corner
    Seconds    = $Seconds
    Text       = $Text
    Installed  = (Get-Date).ToString('s')
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $installDir 'install.json') -Encoding UTF8

Write-Host ""
Write-Host "  Done." -ForegroundColor Green
Write-Host "  Launch with the 'Dolphin (Achievements)' shortcut." -ForegroundColor Gray
Write-Host "  The overlay starts with Dolphin and exits when Dolphin closes." -ForegroundColor Gray
Write-Host "  Log: $installDir\achiever.log" -ForegroundColor DarkGray
Write-Host ""
