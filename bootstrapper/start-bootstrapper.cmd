@echo off
setlocal
chcp 65001 >nul
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0bootstrapper.ps1"
set "EXITCODE=%errorlevel%"
if not "%EXITCODE%"=="0" pause
exit /b %EXITCODE%
