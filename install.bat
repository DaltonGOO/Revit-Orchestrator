@echo off
REM Double-clickable wrapper around install.ps1 for users whose PowerShell
REM execution policy blocks unsigned downloaded scripts (the usual cause of
REM "the file ... is not digitally signed"). install.ps1 itself pauses on
REM completion, so the console window stays open long enough to read.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
