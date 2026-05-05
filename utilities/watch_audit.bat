@echo off
REM Live audit-log viewer. Color-coded one-line summary of every tool call.
title Revit Orchestrator - Audit Log
python "%~dp0watch_audit.py" --follow
echo.
pause
