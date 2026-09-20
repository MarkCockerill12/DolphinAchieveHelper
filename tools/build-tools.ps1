# Builds the development test harnesses in this folder.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$csc  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$src  = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }
$refs = @('-r:System.dll','-r:System.Drawing.dll','-r:System.Windows.Forms.dll','-r:System.Web.Extensions.dll')

& $csc -nologo -platform:x64 -main:TestMatch "-out:$root\tools\TestMatch.exe" @refs @src "$root\tools\TestMatch.cs"
& $csc -nologo -platform:x64 -target:winexe "-out:$root\tools\ToastTest.exe" -r:System.Drawing.dll -r:System.Windows.Forms.dll "$root\src\Toast.cs" "$root\tools\ToastTest.cs"
& $csc -nologo -platform:x64 "-out:$root\tools\ShotWin.exe" -r:System.Drawing.dll "$root\tools\ShotWin.cs"
Write-Host "tools built" -ForegroundColor Green
