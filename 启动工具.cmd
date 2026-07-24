@echo off
cd /d "%~dp0"
if not exist "Microsoft.Web.WebView2.Core.dll" goto missing
if not exist "Microsoft.Web.WebView2.WinForms.dll" goto missing
if not exist "WebView2Loader.dll" goto missing
start "" "侵权链接处置核验工具.exe"
exit /b 0
:missing
echo Tool files are incomplete. Please extract the entire portable package before running.
pause
exit /b 1
