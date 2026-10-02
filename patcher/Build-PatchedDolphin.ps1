<#
.SYNOPSIS
    Patches a Dolphin install so it shows RetroAchievements where you can earn them.

.DESCRIPTION
    Reads which Dolphin the install is, fetches Dolphin's source at exactly that version,
    applies the patches in .\patches, builds it, and copies the changed files into the
    install. Files it replaces are kept in <install>\DolphinAchiever-backup\<time>\.

    Because it always builds the version you already have, it keeps working when you
    update Dolphin: update, then run this again. If a future Dolphin changes the code the
    patches touch, it stops before changing anything and says so.

    PatcherGui.ps1 (started by "Patch Dolphin.cmd") is the friendly front end for this.

.PARAMETER Install
    The Dolphin folder (the one containing Dolphin.exe).

.PARAMETER WorkDir
    Where Dolphin's source is fetched and built. Needs about 4 GB; kept between runs so
    later runs are much faster.

.PARAMETER Ref
    Build this Dolphin tag or commit instead of the one detected from the install.

.PARAMETER AutoUpdate
    On (default): Dolphin keeps updating itself. An update replaces the patched Dolphin.exe
    with the official one, so run this again afterwards to get the achievement list back.
    Off: Dolphin stays on this version until you update it yourself.

.EXAMPLE
    .\Build-PatchedDolphin.ps1 -Install "C:\Dolphin-x64"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Install,
    [string]$WorkDir = "$env:USERPROFILE\dolphin-patched-build",
    [string]$Ref,
    [ValidateSet('On', 'Off')][string]$AutoUpdate = 'On'
)

$ErrorActionPreference = 'Stop'
$clock = [Diagnostics.Stopwatch]::StartNew()
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'PatcherCore.ps1')

trap {
    Write-Host "ERROR: $($_.Exception.Message)"
    exit 1
}

function Step($m) { Write-Host ''; Write-Host "== $m" }
function Ok($m) { Write-Host "  [ok] $m" }
function Say($m) { Write-Host "  $m" }

# Native tools write progress to stderr; only the exit code says whether they failed.
function Invoke-Native([string]$What, [scriptblock]$Command) {
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Command 2>&1 | ForEach-Object { Write-Host "    $_" } }
    finally { $ErrorActionPreference = $old }
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit code $LASTEXITCODE)." }
}

$patches = @('challenge-details.patch', 'level-banner.patch') | ForEach-Object { Join-Path $here "patches\$_" }
# Only for Dolphin versions that are still built from Visual Studio project files (up to 2606a):
# it adds the new source files to those projects. Later versions are built with CMake, which
# level-banner.patch itself covers.
$msbuildPatch = Join-Path $here 'patches\level-banner-msbuild.patch'
foreach ($p in $patches + $msbuildPatch) { if (-not (Test-Path $p)) { throw "Missing patch file: $p" } }

# --- the install ------------------------------------------------------------
Step 'Checking the Dolphin install'
$Install = [IO.Path]::GetFullPath($Install)
if (-not (Test-Path (Join-Path $Install 'Dolphin.exe'))) { throw "No Dolphin.exe in $Install" }
$version = Get-DolphinVersion $Install
if (-not $version) { throw "Could not tell which Dolphin version is in $Install." }
Ok "Dolphin $($version.Name)$(if ($version.Patched) { ' (already patched; it will be rebuilt)' })"
if (Test-DolphinRunning $Install) { throw 'Dolphin is running. Close it and try again.' }

# Find out now, not after an hour of building, whether the install can be written to
# (e.g. Dolphin under C:\Program Files needs administrator rights).
$probe = Join-Path $Install ('.dolphinachiever-write-test-' + [Guid]::NewGuid().ToString('N'))
try { [IO.File]::WriteAllText($probe, 'x'); [IO.File]::Delete($probe) }
catch {
    throw "Can't write to $Install. Run the patcher as administrator (right-click 'Patch Dolphin.cmd' > Run as administrator), or move Dolphin to a folder you own."
}

# The first run downloads Dolphin's source and all its libraries (about 1 GB) and builds them:
# about 4 GB in all. Leave some headroom.
$firstRun = -not (Test-Path (Join-Path $WorkDir 'dolphin\.git'))
$needGB = if ($firstRun) { 6 } else { 2 }
$root = [IO.Path]::GetPathRoot([IO.Path]::GetFullPath($WorkDir))
$freeGB = [Math]::Floor((New-Object IO.DriveInfo $root).AvailableFreeSpace / 1GB)
if ($freeGB -lt $needGB) {
    throw "Only $freeGB GB free on $root; building Dolphin needs about $needGB GB. Free some space, or pass -WorkDir on another drive."
}
Ok "$freeGB GB free on $root"

# --- tools ------------------------------------------------------------------
Step 'Checking tools'
Update-SessionPath
$git = Find-Git
if (-not $git) { throw 'Git is not installed. Install it from https://git-scm.com (or use the button in the patcher window).' }
Ok "Git: $git"
if (-not (Find-MSBuild)) {
    throw "Visual Studio's C++ build tools are not installed. Install 'Visual Studio Build Tools' with the 'Desktop development with C++' workload (or use the button in the patcher window)."
}

# --- source -----------------------------------------------------------------
Step "Getting Dolphin's source"
$src = Join-Path $WorkDir 'dolphin'
# Some bundled libraries (SPIRV-Cross's test files) have paths past Windows' 260-character
# limit once under a work folder; without this, checking them out fails.
$gitArgs = @('-c', 'core.longpaths=true', '-C', $src)
if (-not (Test-Path (Join-Path $src '.git'))) {
    New-Item -ItemType Directory -Force -Path $src | Out-Null
    Invoke-Native 'git init' { & $git @gitArgs init -q }
    Invoke-Native 'git remote' { & $git @gitArgs remote add origin $DolphinRepo }
    Invoke-Native 'git config' { & $git @gitArgs config core.longpaths true }
    Say 'First run: this downloads about 1 GB.'
}

# Candidates, most specific first: an explicit -Ref, the release tag, then commit ids read
# from the exe (a dev build has no tag, and GitHub serves any commit by id).
$candidates = @()
if ($Ref) { $candidates += $Ref }
else {
    if ($version.IsRelease) { $candidates += "refs/tags/$($version.Name)" }
    $candidates += $version.Commits
}
$fetched = $null
foreach ($c in $candidates) {
    Say "Fetching $c ..."
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    & $git @gitArgs fetch --depth 1 --no-tags origin $c 2>&1 | ForEach-Object { Write-Host "    $_" }
    $ErrorActionPreference = $old
    if ($LASTEXITCODE -eq 0) { $fetched = $c; break }
}
if (-not $fetched) { throw "Could not download Dolphin $($version.Name)'s source. Check your internet connection." }

# Throw away the previous run's patches and put the tree exactly at this version.
Invoke-Native 'git checkout' { & $git @gitArgs checkout -q --force FETCH_HEAD }
Invoke-Native 'git clean' { & $git @gitArgs clean -q -fd }
Invoke-Native 'git submodule reset' { & $git @gitArgs submodule foreach -q --recursive 'git reset -q --hard && git clean -q -fd' }
Say 'Updating bundled libraries...'
Invoke-Native 'git submodule update' { & $git @gitArgs submodule update -q --init --recursive --depth 1 --jobs 8 }
Ok "Source ready at $((& $git @gitArgs rev-parse --short HEAD).Trim())"

# Dolphin names itself with `git describe`, which needs a tag, and only the one commit was
# downloaded. Without this the build would call itself by its commit id and the next run could
# not tell which version is installed.
if (-not $Ref) {
    Invoke-Native 'git tag' {
        & $git @gitArgs -c user.name=DolphinAchiever -c user.email=patcher@localhost tag -a -f -m $version.Name $version.Name HEAD
    }
}

# Dolphin up to 2606a is built from a Visual Studio solution; later versions with CMake.
$usesMSBuild = Test-Path (Join-Path $src 'Source\dolphin-emu.sln')
if ($usesMSBuild) { $patches += $msbuildPatch }

# --- patches ----------------------------------------------------------------
Step 'Applying patches'
foreach ($p in $patches) {
    $name = Split-Path -Leaf $p
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    & $git @gitArgs apply --3way $p 2>&1 | ForEach-Object { Write-Host "    $_" }
    $ErrorActionPreference = $old
    if ($LASTEXITCODE -ne 0) {
        throw "$name does not fit Dolphin $($version.Name): Dolphin changed the code it modifies. Nothing in your install was touched. The patches need updating for this version."
    }
    Ok $name
}

# --- build ------------------------------------------------------------------
Step 'Building (about 10 minutes on a fast laptop, 20-40 on a slower one; ~1 minute if nothing changed)'
if ($usesMSBuild) {
    $toolset = 'v143'
    $propsFile = Join-Path $src 'Source\VSProps\Configuration.Base.props'
    if (Test-Path $propsFile) {
        $m = [regex]::Match((Get-Content -Raw $propsFile), '<PlatformToolset>(v\d+)</PlatformToolset>')
        if ($m.Success) { $toolset = $m.Groups[1].Value }
    }
    $vs = Find-MSBuild $toolset
    if (-not $vs) {
        throw "Dolphin $($version.Name) needs Visual C++ toolset $toolset, which none of your Visual Studio installs has. Install the Visual Studio Build Tools that provide it ('Desktop development with C++')."
    }
    Ok "$($vs.Name) ($toolset)"
    Invoke-Native 'The build' {
        & $vs.MSBuild (Join-Path $src 'Source\dolphin-emu.sln') -p:Configuration=Release -p:Platform=x64 -m -v:minimal -nologo
    }
    $bin = Join-Path $src 'Binary\x64'
} else {
    # CMake + Ninja with the Visual C++ compiler. Dolphin refuses to compile with one older than
    # it was developed against (an #error in its precompiled header, e.g. "_MSC_FULL_VER <
    # 195136252" = 19.51), so check that here rather than fail ten minutes into the build.
    $need = [Version]'19.0.0'
    $pch = Join-Path $src 'Source\PCH\pch.h'
    if (Test-Path $pch) {
        $m = [regex]::Match((Get-Content -Raw $pch), '_MSC_FULL_VER\s*<\s*(\d{2})(\d{2})(\d{5})')
        if ($m.Success) { $need = New-Object Version ([int]$m.Groups[1].Value), ([int]$m.Groups[2].Value), ([int]$m.Groups[3].Value) }
    }
    $vs = Find-VCCompiler
    $have = if ($vs) { $vs.Version } else { $null }
    if (-not $vs -or $have -lt $need) {
        $mine = if ($vs) { "Yours is $have ($($vs.Name))." } else { 'You have none installed.' }
        throw "Dolphin $($version.Name) needs the Visual C++ compiler $need or newer. $mine Install or update 'Visual Studio Build Tools 2026' with the 'Desktop development with C++' workload (https://visualstudio.microsoft.com/visual-cpp-build-tools/), then run again."
    }
    $vcvars = Join-Path $vs.Root 'VC\Auxiliary\Build\vcvars64.bat'

    # CMake and Ninja come with the C++ workload ("C++ CMake tools for Windows"); otherwise
    # take them from PATH.
    $vsCMake = 'Common7\IDE\CommonExtensions\Microsoft\CMake'
    $cmake = Find-VsTool $vs.Root "$vsCMake\CMake\bin\cmake.exe" 'cmake.exe'
    $ninja = Find-VsTool $vs.Root "$vsCMake\Ninja\ninja.exe" 'ninja.exe'
    if (-not $cmake -or -not $ninja) {
        throw "Dolphin $($version.Name) is built with CMake and Ninja, which were not found. In the Visual Studio Installer, modify $($vs.Name) and tick 'C++ CMake tools for Windows'."
    }
    Ok "$($vs.Name) (Visual C++ $have), CMake + Ninja"

    # CMake remembers the compiler it was first configured with. After a Visual Studio update
    # or a newer install that would keep building with the old one, so start afresh then.
    $buildDir = Join-Path $src 'build\release\x64'
    $cache = Join-Path $buildDir 'CMakeCache.txt'
    if (Test-Path $cache) {
        $m = [regex]::Match((Get-Content -Raw $cache), '(?m)^CMAKE_CXX_COMPILER:[A-Z]+=(.*)$')
        $cached = if ($m.Success) { [IO.Path]::GetDirectoryName($m.Groups[1].Value.Trim().Replace('/', '\')) } else { '' }
        $current = Get-ChildItem -Path (Join-Path $vs.Root 'VC\Tools\MSVC') -Directory |
            ForEach-Object { Join-Path $_.FullName 'bin\Hostx64\x64' }
        if ($current -notcontains $cached) {
            Say 'The compiler changed since the last build; rebuilding from scratch.'
            Remove-Item -LiteralPath $buildDir -Recurse -Force
        }
    }

    # The compiler environment comes from a batch file, so the build runs from one too.
    $buildCmd = Join-Path $WorkDir 'build-dolphin.cmd'
    [IO.File]::WriteAllLines($buildCmd, @(
        '@echo off',
        "call `"$vcvars`" >nul || exit /b 1",
        "cd /d `"$src`" || exit /b 1",
        "`"$cmake`" --preset ninja-release-x64 -DCMAKE_MAKE_PROGRAM=`"$($ninja.Replace('\', '/'))`" -DENABLE_TESTS=OFF || exit /b 1",
        "`"$cmake`" --build --preset ninja-build-release-x64 --target dolphin-emu || exit /b 1"
    ), [Text.Encoding]::GetEncoding([Globalization.CultureInfo]::CurrentCulture.TextInfo.OEMCodePage))
    Invoke-Native 'The build' { & $env:ComSpec /d /c $buildCmd }
    $bin = Join-Path $buildDir 'Binaries'
}
if (-not (Test-Path (Join-Path $bin 'Dolphin.exe'))) { throw "The build finished but $bin\Dolphin.exe is missing." }
Ok 'Built'

# --- install ----------------------------------------------------------------
Step "Installing into $Install"
if (Test-DolphinRunning $Install) { throw 'Dolphin was started during the build. Close it and run again.' }
function Get-Sha256([string]$Path) {
    $sha = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($Path)
    try { return [BitConverter]::ToString($sha.ComputeHash($stream)) }
    finally { $stream.Dispose(); $sha.Dispose() }
}

# The build is the same version as the install, so the patched Dolphin.exe is the only file
# that really differs (the rest would only differ in line endings from git). With -Ref it is
# a different version, so the whole program is brought along.
$files = if ($Ref) { [IO.Directory]::GetFiles($bin, '*', 'AllDirectories') } else { @(Join-Path $bin 'Dolphin.exe') }

# Work out every change before copying anything, so a problem cannot leave a half-patched
# install behind.
$changes = @()
foreach ($file in $files) {
    $rel = $file.Substring($bin.Length + 1)
    # Never touch settings, saves or the portable marker.
    if ($rel -like 'User\*' -or $rel -eq 'portable.txt') { continue }
    $target = Join-Path $Install $rel
    $exists = [IO.File]::Exists($target)
    if ($exists -and (New-Object IO.FileInfo $target).Length -eq (New-Object IO.FileInfo $file).Length -and
        (Get-Sha256 $target) -eq (Get-Sha256 $file)) { continue }
    $changes += [pscustomobject]@{ Source = $file; Target = $target; Rel = $rel; Exists = $exists }
}

$backup = Join-Path $Install ('DolphinAchiever-backup\' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss'))
foreach ($c in $changes | Where-Object { $_.Exists }) {
    $keep = Join-Path $backup $c.Rel
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($keep))
    [IO.File]::Copy($c.Target, $keep, $true)
}
foreach ($c in $changes) {
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($c.Target))
    [IO.File]::Copy($c.Source, $c.Target, $true)
}
Ok "$($changes.Count) file(s) updated"
if ([IO.Directory]::Exists($backup)) { Ok "Replaced files kept in $backup" }

# Dolphin's update setting defaults to what the exe was built with: the official builds say
# "update", a self-built one says "don't". So the choice has to be written down explicitly.
foreach ($ini in Get-DolphinIniPaths $Install) {
    $text = if (Test-Path -LiteralPath $ini) { [IO.File]::ReadAllText($ini) } else { '' }
    $current = Get-IniValue $text 'AutoUpdate' 'UpdateTrack'
    if ($AutoUpdate -eq 'On') {
        # Keep a channel the user already picked; otherwise follow the kind of build installed.
        $track = if ($current) { $current } elseif ($version.IsRelease) { 'beta' } else { 'dev' }
        $label = if ($track -eq 'dev') { 'dev builds' } else { 'releases' }
        Set-IniValue $ini 'AutoUpdate' 'UpdateTrack' $track
        Ok "Auto-update stays on ($label). After Dolphin updates itself, run this again."
    } else {
        Set-IniValue $ini 'AutoUpdate' 'UpdateTrack' ''
        Ok 'Auto-update turned off'
    }
}

# The list can also be held up with a hotkey. Dolphin only applies its built-in hotkey defaults
# when there is no Hotkeys.ini, and any Dolphin that has been started has one, so the new hotkey
# would begin unbound. Bind it when it has no binding; one the user chose is left alone.
$hotkey = 'General/Show Level Achievements'
foreach ($ini in Get-DolphinIniPaths $Install) {
    $hotkeys = Join-Path (Split-Path -Parent $ini) 'Hotkeys.ini'
    if (-not (Test-Path -LiteralPath $hotkeys)) { continue }
    if ($null -eq (Get-IniValue ([IO.File]::ReadAllText($hotkeys)) 'Hotkeys' $hotkey)) {
        Set-IniValue $hotkeys 'Hotkeys' $hotkey '`DInput/0/Keyboard Mouse:APOSTROPHE`'
        Ok "Hold ' (apostrophe) in a game to show the level's achievements (Options > Hotkey Settings to change)"
    }
}

Write-Host ''
Write-Host ('Finished in {0:0} min.' -f $clock.Elapsed.TotalMinutes)
Write-Host 'DONE: Dolphin is patched. Start it as usual.'
exit 0
