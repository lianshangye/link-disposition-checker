@echo off
cd /d "%~dp0"
if exist "NetworkDiagnostics.exe" goto run
echo NetworkDiagnostics.exe is missing. Please extract the entire ZIP.
pause
exit /b 1
:run
"NetworkDiagnostics.exe"
