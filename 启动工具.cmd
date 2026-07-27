@echo off
setlocal
cd /d "%~dp0"
if not exist "StartupCheck.exe" goto missing
"StartupCheck.exe"
exit /b %errorlevel%

:missing
echo StartupCheck.exe is missing.
echo Please extract the entire ZIP before running this tool.
pause
exit /b 1
