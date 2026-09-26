@echo off
setlocal EnableExtensions

set "INSTALL_PATH=$INSTALL_PATH"
set "APP_VERSION=$APP_VER"
set "UNINSTALLER_JAR=%INSTALL_PATH%\Uninstaller\uninstaller.jar"
set "UNINSTALL_SCRIPT=%INSTALL_PATH%\Uninstaller\M2M XML EDITOR eltavolitasa.ps1"
set "JAVA_HOME=%LOCALAPPDATA%\M2M-XML-EDITOR-Online-Setup\runtime\jdk-17"
set "JAVAW_EXE=%JAVA_HOME%\bin\javaw.exe"

if not exist "%UNINSTALL_SCRIPT%" exit /b 4

set "TEMP_UNINSTALL_PS=%TEMP%\M2M-XML-EDITOR-uninstall-%RANDOM%-%RANDOM%.ps1"
copy /Y "%UNINSTALL_SCRIPT%" "%TEMP_UNINSTALL_PS%" >nul
if errorlevel 1 exit /b 5

start "M2M XML EDITOR uninstall" powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%TEMP_UNINSTALL_PS%" -InstallPath "%INSTALL_PATH%" -AppVersion "%APP_VERSION%" -JavawExe "%JAVAW_EXE%" -UninstallerJar "%UNINSTALLER_JAR%"
exit /b 0
