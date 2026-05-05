@echo off
REM Live server-log viewer. Highlights pipe lifecycle, tool registration,
REM LLM tool catalogs, and errors.
title Revit Orchestrator - Server Log
python "%~dp0watch_server.py" --follow
echo.
pause
