# Shared by Build-PatchedDolphin.ps1 (does the work) and PatcherGui.ps1 (the window).
# Windows PowerShell 5.1 compatible: this is what runs when someone double-clicks the .cmd.

$script:DolphinRepo = 'https://github.com/dolphin-emu/dolphin.git'

# Reads which Dolphin an install is. Dolphin.exe has no version resource, but the build
# embeds its version name ("Dolphin 2606a", "Dolphin 2606-123" for dev builds) and the git
# commit it was built from, which is what lets us build exactly the same source.
function Get-DolphinVersion([string]$InstallDir) {
    $exe = Join-Path $InstallDir 'Dolphin.exe'
    if (-not (Test-Path -LiteralPath $exe)) { return $null }

    $text = [Text.Encoding]::GetEncoding(28591).GetString([IO.File]::ReadAllBytes($exe))
    # Since mid-2024 versions are named by date ("2407", "2606a"); older ones "5.0-21460".
    $m = [regex]::Match($text, 'Dolphin ((?:\d{4}[a-z]?|\d+\.\d+)(?:-\d+)?)(-dirty)?\x00')
    if (-not $m.Success) { return $null }

    # 40-hex strings that look like a commit id (lookup tables in the binary are digit runs or
    # repeated patterns, so require letters, digits and variety).
    $commits = New-Object System.Collections.Generic.List[string]
    foreach ($c in [regex]::Matches($text, '(?<![0-9a-f])[0-9a-f]{40}(?![0-9a-f])')) {
        $v = $c.Value
        if ($v -notmatch '[a-f]' -or $v -notmatch '[0-9]') { continue }
        if (($v.ToCharArray() | Select-Object -Unique).Count -lt 12) { continue }
        if (-not $commits.Contains($v)) { $commits.Add($v) }
    }

    $name = $m.Groups[1].Value
    [pscustomobject]@{
        Name      = $name
        # A release ("2606a") is a git tag; a dev build ("2606-123") is only a commit.
        IsRelease = $name -match '^(\d{4}[a-z]?|\d+\.\d+)$'
        Patched   = $m.Groups[2].Success
        Commits   = $commits
    }
}

function Update-SessionPath {
    # Pick up tools installed since this window opened.
    $machine = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $user = [Environment]::GetEnvironmentVariable('Path', 'User')
    $env:Path = ($machine, $user | Where-Object { $_ }) -join ';'
}

function Find-Git {
    $cmd = Get-Command git.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    foreach ($p in @("$env:ProgramFiles\Git\cmd\git.exe", "${env:ProgramFiles(x86)}\Git\cmd\git.exe")) {
        if (Test-Path $p) { return $p }
    }
    return $null
}

function Get-VsInstalls {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $vswhere)) { return @() }
    $json = & $vswhere -products '*' -all -prerelease -format json 2>$null
    if (-not $json) { return @() }
    # Windows PowerShell hands back a JSON array as one object; unroll it.
    return @($json | Out-String | ConvertFrom-Json | ForEach-Object { $_ })
}

# MSBuild from a Visual Studio install that has the given C++ toolset (v143 = VS 2022). Without
# -Toolset, any install with some C++ toolset. Dolphin's projects pin a toolset, and a newer
# Dolphin may need a newer Visual Studio, so this checks for the exact one.
function Find-MSBuild([string]$Toolset) {
    foreach ($vs in Get-VsInstalls) {
        $root = $vs.installationPath
        $msbuild = Join-Path $root 'MSBuild\Current\Bin\MSBuild.exe'
        if (-not (Test-Path $msbuild)) { continue }
        $pattern = if ($Toolset) { $Toolset } else { 'v*' }
        $found = Get-ChildItem -Path (Join-Path $root 'MSBuild\Microsoft\VC') -Directory -ErrorAction SilentlyContinue |
            ForEach-Object { Join-Path $_.FullName 'Platforms\x64\PlatformToolsets' } |
            Where-Object { Test-Path $_ } |
            ForEach-Object { Get-ChildItem -Path $_ -Directory -Filter $pattern -ErrorAction SilentlyContinue } |
            Select-Object -First 1
        if ($found) {
            return [pscustomobject]@{ MSBuild = $msbuild; Name = $vs.displayName; Toolset = $found.Name }
        }
    }
    return $null
}

# Every place a Dolphin install may keep its settings.
function Get-DolphinUserDirs([string]$InstallDir) {
    $dirs = @()
    if (Test-Path (Join-Path $InstallDir 'portable.txt')) { $dirs += (Join-Path $InstallDir 'User') }
    $dirs += (Join-Path $env:APPDATA 'Dolphin Emulator')
    $dirs += (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Dolphin Emulator')
    return $dirs | Where-Object { Test-Path (Join-Path $_ 'Config\Dolphin.ini') }
}

# The Dolphin.ini files this install uses, creating the one Dolphin will read if none exists yet
# (a fresh install that has never been started).
function Get-DolphinIniPaths([string]$InstallDir) {
    $dirs = @(Get-DolphinUserDirs $InstallDir)
    if ($dirs.Count -eq 0) {
        $dirs = @($(if (Test-Path (Join-Path $InstallDir 'portable.txt')) { Join-Path $InstallDir 'User' }
                    else { Join-Path $env:APPDATA 'Dolphin Emulator' }))
    }
    return $dirs | ForEach-Object { Join-Path $_ 'Config\Dolphin.ini' }
}

function Get-IniValue([string]$Text, [string]$Section, [string]$Key) {
    $m = [regex]::Match($Text, "(?ms)^\[$([regex]::Escape($Section))\][^\[]*?^$([regex]::Escape($Key)) = ([^\r\n]*)")
    if ($m.Success) { return $m.Groups[1].Value.Trim() }
    return $null
}

function Set-IniValue([string]$Path, [string]$Section, [string]$Key, [string]$Value) {
    $text = if (Test-Path -LiteralPath $Path) { [IO.File]::ReadAllText($Path) } else { '' }
    $sec = [regex]::Escape($Section)
    $k = [regex]::Escape($Key)
    if ([regex]::IsMatch($text, "(?ms)^\[$sec\][^\[]*?^$k = ")) {
        $text = [regex]::Replace($text, "(?ms)(^\[$sec\][^\[]*?^$k = )[^\r\n]*", { param($m) $m.Groups[1].Value + $Value })
    } elseif ([regex]::IsMatch($text, "(?m)^\[$sec\](?=\r?$)")) {
        $text = [regex]::Replace($text, "(?m)^\[$sec\](?=\r?$)", { param($m) $m.Value + "`r`n$Key = $Value" })
    } else {
        if ($text -and -not $text.EndsWith("`n")) { $text += "`r`n" }
        $text += "[$Section]`r`n$Key = $Value`r`n"
    }
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))
    [IO.File]::WriteAllText($Path, $text)
}

function Test-DolphinRunning([string]$InstallDir) {
    $exe = (Join-Path $InstallDir 'Dolphin.exe')
    foreach ($p in Get-Process -Name Dolphin -ErrorAction SilentlyContinue) {
        try { if ($p.Path -and ([IO.Path]::GetFullPath($p.Path) -eq [IO.Path]::GetFullPath($exe))) { return $true } }
        catch { return $true }
    }
    return $false
}
