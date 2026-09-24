@echo off
rem Opens the Dolphin Achiever patcher window.
rem Clear PowerShell 7 module paths inherited from a terminal; Windows PowerShell uses its own.
set PSModulePath=
start "" powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0patcher\PatcherGui.ps1"
