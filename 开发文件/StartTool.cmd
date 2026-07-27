@echo off
setlocal
cd /d "%~dp0"
if exist "StartupCheck.exe" goto portable
if exist "prepare-runtime.ps1" goto source
goto missing

:portable
"StartupCheck.exe"
exit /b %errorlevel%

:source
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0prepare-runtime.ps1" -Launch
if errorlevel 1 pause
exit /b %errorlevel%

:missing
echo StartupCheck.exe is missing.
echo Please extract the entire ZIP before running this tool.
pause
exit /b 1
