@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0prepare-runtime.ps1" -Launch
if errorlevel 1 pause
exit /b %errorlevel%
