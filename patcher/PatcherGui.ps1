# The patcher window. Started by "Patch Dolphin.cmd"; the work itself is done by
# Build-PatchedDolphin.ps1, run as a child process whose output is shown live here.
# Windows PowerShell 5.1 compatible.

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'PatcherCore.ps1')

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -Namespace Native -Name Dpi -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();'
[Native.Dpi]::SetProcessDPIAware() | Out-Null
[Windows.Forms.Application]::EnableVisualStyles()

$engine = Join-Path $here 'Build-PatchedDolphin.ps1'
$state = @{ Process = $null; Log = $null; Offset = 0L; Version = $null }

# Fonts follow the display scaling on their own; sizes in pixels have to be scaled by hand.
$screen = [Drawing.Graphics]::FromHwnd([IntPtr]::Zero)
$scale = $screen.DpiX / 96.0
$screen.Dispose()
function Px([int]$n) { return [int][Math]::Round($n * $scale) }

# --- layout -----------------------------------------------------------------
$form = New-Object Windows.Forms.Form
$form.Text = 'Dolphin Achiever - Patch Dolphin'
$form.AutoScaleMode = 'None'
$form.Font = New-Object Drawing.Font('Segoe UI', 9)
$form.StartPosition = 'CenterScreen'
$form.ClientSize = New-Object Drawing.Size((Px 760), (Px 700))
$form.MinimumSize = New-Object Drawing.Size((Px 640), (Px 520))

$table = New-Object Windows.Forms.TableLayoutPanel
$table.Dock = 'Fill'
$table.Padding = New-Object Windows.Forms.Padding((Px 12))
$table.ColumnCount = 3
[void]$table.ColumnStyles.Add((New-Object Windows.Forms.ColumnStyle('AutoSize')))
[void]$table.ColumnStyles.Add((New-Object Windows.Forms.ColumnStyle('Percent', 100)))
[void]$table.ColumnStyles.Add((New-Object Windows.Forms.ColumnStyle('AutoSize')))
$form.Controls.Add($table)

function Add-Row($control, [int]$column = 0, [int]$span = 3, [string]$height = 'AutoSize') {
    $row = $table.RowCount
    $table.RowCount = $row + 1
    [void]$table.RowStyles.Add((New-Object Windows.Forms.RowStyle($height, 100)))
    $table.Controls.Add($control, $column, $row)
    $table.SetColumnSpan($control, $span)
    return $row
}

function New-Label([string]$text) {
    $l = New-Object Windows.Forms.Label
    $l.Text = $text
    $l.AutoSize = $true
    $l.MaximumSize = New-Object Drawing.Size((Px 730), 0)
    $l.Margin = New-Object Windows.Forms.Padding((Px 3), (Px 4), (Px 3), (Px 4))
    return $l
}

$title = New-Label 'Patch Dolphin to show RetroAchievements where you can earn them'
$title.Font = New-Object Drawing.Font('Segoe UI', 12, [Drawing.FontStyle]::Bold)
[void](Add-Row $title)

[void](Add-Row (New-Label ("When you enter a level, Dolphin will list the achievements you can still earn " +
    "there, and label its challenge indicators. This builds Dolphin from source at the same version " +
    "you already have, so the first run downloads a few GB and takes 30-60 minutes. Later runs take minutes.")))

$folderLabel = New-Label 'Dolphin folder:'
$folderLabel.Anchor = 'Left'
$folderBox = New-Object Windows.Forms.TextBox
$folderBox.Dock = 'Fill'
$browse = New-Object Windows.Forms.Button
$browse.Text = 'Browse...'
$browse.AutoSize = $true
$r = $table.RowCount
$table.RowCount = $r + 1
[void]$table.RowStyles.Add((New-Object Windows.Forms.RowStyle('AutoSize')))
$table.Controls.Add($folderLabel, 0, $r)
$table.Controls.Add($folderBox, 1, $r)
$table.Controls.Add($browse, 2, $r)

$versionLabel = New-Label ''
[void](Add-Row $versionLabel)

$toolsLabel = New-Label ''
$installTools = New-Object Windows.Forms.Button
$installTools.Text = 'Install missing tools'
$installTools.AutoSize = $true
$r = $table.RowCount
$table.RowCount = $r + 1
[void]$table.RowStyles.Add((New-Object Windows.Forms.RowStyle('AutoSize')))
$table.Controls.Add($toolsLabel, 0, $r)
$table.SetColumnSpan($toolsLabel, 2)
$table.Controls.Add($installTools, 2, $r)

$autoUpdate = New-Object Windows.Forms.CheckBox
$autoUpdate.Text = "Keep Dolphin's auto-update on"
$autoUpdate.AutoSize = $true
$autoUpdate.Checked = $true
$autoUpdate.Margin = New-Object Windows.Forms.Padding((Px 3), (Px 8), (Px 3), (Px 0))
[void](Add-Row $autoUpdate)

$autoUpdateNote = New-Label ''
$autoUpdateNote.ForeColor = [Drawing.Color]::DimGray
$autoUpdateNote.Margin = New-Object Windows.Forms.Padding((Px 22), (Px 0), (Px 3), (Px 6))
[void](Add-Row $autoUpdateNote)

function Update-AutoUpdateNote {
    $autoUpdateNote.Text = $(if ($autoUpdate.Checked) {
        "Dolphin keeps updating itself as normal. Each update brings back the standard Dolphin.exe, " +
        "so the achievement list stops showing until you run this patcher again (a few minutes)."
    } else {
        "Dolphin stays on this version. To update later, install the new Dolphin, then run this patcher again."
    })
}
Update-AutoUpdateNote

$backupNote = New-Label ("Before you run it: make a backup of your Dolphin folder (copy the whole folder " +
    "somewhere safe). The patcher never touches your saves or settings and keeps a copy of every file " +
    "it replaces, but a backup is the easy way back if anything goes wrong.")
$backupNote.BackColor = [Drawing.Color]::FromArgb(255, 244, 206)
$backupNote.Padding = New-Object Windows.Forms.Padding((Px 6))
$backupNote.Dock = 'Fill'
[void](Add-Row $backupNote)

$buttons = New-Object Windows.Forms.FlowLayoutPanel
$buttons.AutoSize = $true
$buttons.Dock = 'Fill'
$run = New-Object Windows.Forms.Button
$run.Text = 'Run'
$run.AutoSize = $true
$run.Font = New-Object Drawing.Font('Segoe UI', 10, [Drawing.FontStyle]::Bold)
$cancel = New-Object Windows.Forms.Button
$cancel.Text = 'Cancel'
$cancel.AutoSize = $true
$cancel.Enabled = $false
$status = New-Label ''
$status.Anchor = 'Left'
$buttons.Controls.AddRange(@($run, $cancel, $status))
[void](Add-Row $buttons)

$progress = New-Object Windows.Forms.ProgressBar
$progress.Dock = 'Fill'
$progress.Style = 'Marquee'
$progress.MarqueeAnimationSpeed = 0
[void](Add-Row $progress)

$log = New-Object Windows.Forms.TextBox
$log.Multiline = $true
$log.ReadOnly = $true
$log.ScrollBars = 'Vertical'
$log.Dock = 'Fill'
$log.Font = New-Object Drawing.Font('Consolas', 9)
$log.BackColor = [Drawing.Color]::White
[void](Add-Row $log 0 3 'Percent')

# --- state ------------------------------------------------------------------
function Test-Tools {
    Update-SessionPath
    return [pscustomobject]@{ Git = Find-Git; VS = Find-MSBuild }
}

function Update-State {
    if ($state.Process) { return }
    $dir = $folderBox.Text.Trim().Trim('"')
    $state.Version = $null
    if ($dir -and (Test-Path -LiteralPath (Join-Path $dir 'Dolphin.exe'))) {
        try { $state.Version = Get-DolphinVersion $dir } catch { }
        if ($state.Version) {
            $v = $state.Version
            $versionLabel.Text = "Found Dolphin $($v.Name)" +
                $(if ($v.Patched) { ' (already patched - running again rebuilds it, e.g. after a Dolphin update).' } else { '.' })
            $versionLabel.ForeColor = [Drawing.Color]::DarkGreen
        } else {
            $versionLabel.Text = 'Found Dolphin.exe but could not tell its version.'
            $versionLabel.ForeColor = [Drawing.Color]::DarkRed
        }
    } elseif ($dir) {
        $versionLabel.Text = 'No Dolphin.exe in that folder. Pick the folder that contains Dolphin.exe.'
        $versionLabel.ForeColor = [Drawing.Color]::DarkRed
    } else {
        $versionLabel.Text = 'Pick the folder that contains Dolphin.exe.'
        $versionLabel.ForeColor = [Drawing.Color]::Black
    }

    $tools = Test-Tools
    $missing = @()
    if (-not $tools.Git) { $missing += 'Git' }
    if (-not $tools.VS) { $missing += 'Visual Studio C++ Build Tools' }
    if ($missing.Count -eq 0) {
        $toolsLabel.Text = "Tools: Git and $($tools.VS.Name) found."
        $toolsLabel.ForeColor = [Drawing.Color]::DarkGreen
    } else {
        $toolsLabel.Text = "Needed to build Dolphin, not installed: $($missing -join ', ')."
        $toolsLabel.ForeColor = [Drawing.Color]::DarkRed
    }
    $installTools.Visible = $missing.Count -gt 0
    $run.Enabled = [bool]$state.Version -and $missing.Count -eq 0
}

function Append-Log {
    if (-not $state.Log -or -not (Test-Path $state.Log)) { return }
    $fs = New-Object IO.FileStream($state.Log, 'Open', 'Read', 'ReadWrite')
    try {
        if ($fs.Length -le $state.Offset) { return }
        [void]$fs.Seek($state.Offset, 'Begin')
        $reader = New-Object IO.StreamReader($fs)
        $new = $reader.ReadToEnd()
        $state.Offset = $fs.Length
    } finally { $fs.Dispose() }
    $log.AppendText(($new -replace "(?<!`r)`n", "`r`n"))
    $steps = [regex]::Matches($new, '(?m)^== (.+)$')
    if ($steps.Count) { $status.Text = $steps[$steps.Count - 1].Groups[1].Value.Trim() }
}

function Set-Running([bool]$running) {
    $run.Enabled = -not $running
    $cancel.Enabled = $running
    $browse.Enabled = -not $running
    $autoUpdate.Enabled = -not $running
    $folderBox.Enabled = -not $running
    $progress.MarqueeAnimationSpeed = $(if ($running) { 30 } else { 0 })
}

$timer = New-Object Windows.Forms.Timer
$timer.Interval = 500
$timer.Add_Tick({
    Append-Log
    $p = $state.Process
    if ($p -and $p.HasExited) {
        $timer.Stop()
        Start-Sleep -Milliseconds 200
        Append-Log
        $code = $p.ExitCode
        $state.Process = $null
        Set-Running $false
        if ($code -eq 0) {
            $status.Text = 'Done'
            $msg = 'Dolphin is patched. Start it as usual.'
            if ($autoUpdate.Checked) {
                $msg += "`n`nAuto-update is on. When Dolphin updates itself, run this patcher again to bring the achievement list back."
            }
            [void][Windows.Forms.MessageBox]::Show($form, $msg, 'Done', 'OK', 'Information')
        } else {
            $status.Text = 'Failed'
            $err = [regex]::Matches($log.Text, '(?m)^ERROR: (.+)$')
            $msg = $(if ($err.Count) { $err[$err.Count - 1].Groups[1].Value } else { "Stopped (exit code $code). See the log for details." })
            [void][Windows.Forms.MessageBox]::Show($form, $msg, 'Patching did not finish', 'OK', 'Warning')
        }
        Update-State
    }
})

# --- events -----------------------------------------------------------------
$browse.Add_Click({
    $dlg = New-Object Windows.Forms.FolderBrowserDialog
    $dlg.Description = 'Select the folder that contains Dolphin.exe'
    if ($folderBox.Text -and (Test-Path $folderBox.Text)) { $dlg.SelectedPath = $folderBox.Text }
    if ($dlg.ShowDialog($form) -eq 'OK') { $folderBox.Text = $dlg.SelectedPath; Update-State }
})
$folderBox.Add_Leave({ Update-State })
$autoUpdate.Add_CheckedChanged({ Update-AutoUpdateNote })
$folderBox.Add_KeyDown({ if ($_.KeyCode -eq 'Enter') { Update-State } })
$form.Add_Activated({ Update-State })

$installTools.Add_Click({
    if (-not (Get-Command winget.exe -ErrorAction SilentlyContinue)) {
        Start-Process 'https://git-scm.com/download/win'
        Start-Process 'https://visualstudio.microsoft.com/visual-cpp-build-tools/'
        [void][Windows.Forms.MessageBox]::Show($form, ("Install Git, and Visual Studio Build Tools with the " +
            "'Desktop development with C++' workload, from the pages just opened. Then come back here."), 'Install tools')
        return
    }
    $tools = Test-Tools
    $cmds = @()
    if (-not $tools.Git) { $cmds += 'winget install --id Git.Git -e --accept-source-agreements --accept-package-agreements' }
    if (-not $tools.VS) {
        $cmds += ('winget install --id Microsoft.VisualStudio.2022.BuildTools -e --accept-source-agreements ' +
            '--accept-package-agreements --override "--passive --wait --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"')
    }
    $answer = [Windows.Forms.MessageBox]::Show($form, ("This installs the missing tools with winget (Windows asks for " +
        "permission; Visual Studio Build Tools is a few GB and takes a while). Continue?"), 'Install tools', 'YesNo', 'Question')
    if ($answer -ne 'Yes') { return }
    $script = ($cmds -join ' & ') + ' & echo. & echo Finished. Close this window and go back to the patcher. & pause'
    Start-Process cmd.exe -ArgumentList '/c', $script
})

$run.Add_Click({
    Update-State
    if (-not $run.Enabled) { return }
    $dir = $folderBox.Text.Trim().Trim('"')
    if (Test-DolphinRunning $dir) {
        [void][Windows.Forms.MessageBox]::Show($form, 'Close Dolphin first, then press Run again.', 'Dolphin is running')
        return
    }
    $answer = [Windows.Forms.MessageBox]::Show($form, ("Have you made a backup of your Dolphin folder?`n`n$dir`n`n" +
        "If not, press No, copy that folder somewhere safe, then press Run again."), 'Backup', 'YesNo', 'Warning')
    if ($answer -ne 'Yes') { return }

    $state.Log = Join-Path $env:TEMP ("DolphinAchiever-patch-{0}.log" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
    $state.Offset = 0L
    $log.Clear()
    $log.AppendText("Log file: $($state.Log)`r`n")
    Update-SessionPath
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = 'cmd.exe'
    $psi.Arguments = ('/c powershell.exe -NoProfile -ExecutionPolicy Bypass -File "{0}" -Install "{1}" -AutoUpdate {2} > "{3}" 2>&1' -f
        $engine, $dir.TrimEnd('\'), $(if ($autoUpdate.Checked) { 'On' } else { 'Off' }), $state.Log)
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $state.Process = [Diagnostics.Process]::Start($psi)
    Set-Running $true
    $status.Text = 'Starting...'
    $timer.Start()
})

$cancel.Add_Click({
    $p = $state.Process
    if (-not $p) { return }
    $answer = [Windows.Forms.MessageBox]::Show($form, 'Stop patching? Your Dolphin install is only changed in the very last step.', 'Cancel', 'YesNo')
    if ($answer -eq 'Yes') { & taskkill.exe /T /F /PID $p.Id | Out-Null }
})

$form.Add_FormClosing({
    if ($state.Process -and -not $state.Process.HasExited) {
        $answer = [Windows.Forms.MessageBox]::Show($form, 'Patching is still running. Stop it and close?', 'Close', 'YesNo')
        if ($answer -ne 'Yes') { $_.Cancel = $true; return }
        & taskkill.exe /T /F /PID $state.Process.Id | Out-Null
    }
})

# Start with the most likely install.
foreach ($guess in @('C:\Dolphin-x64', "$env:ProgramFiles\Dolphin-x64", "$env:ProgramFiles\Dolphin",
                     "${env:ProgramFiles(x86)}\Dolphin", "$env:LOCALAPPDATA\Programs\Dolphin")) {
    if (Test-Path (Join-Path $guess 'Dolphin.exe')) { $folderBox.Text = $guess; break }
}

[void]$form.ShowDialog()
