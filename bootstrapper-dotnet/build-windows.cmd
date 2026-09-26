@echo off
setlocal
cd /d "%~dp0"

set "BUNDLE_RUNTIME_COMPONENTS=false"
if /I "%~1"=="--bundle-runtime-components" set "BUNDLE_RUNTIME_COMPONENTS=true"
if /I "%~1"=="--online-runtime-components" set "BUNDLE_RUNTIME_COMPONENTS=false"
if not "%~2"=="" (
  echo HIBA: Ismeretlen build parameter: %~2
  exit /b 1
)
if not "%~1"=="" if /I not "%~1"=="--bundle-runtime-components" if /I not "%~1"=="--online-runtime-components" (
  echo HIBA: Ismeretlen build parameter: %~1
  echo Hasznalat: build-windows.cmd [--bundle-runtime-components^|--online-runtime-components]
  exit /b 1
)

if exist "%~dp0bootstrapper-dotnet\App.axaml" (
  echo HIBA: Duplikalt bootstrapper forrasmappa talalhato: "%~dp0bootstrapper-dotnet"
  echo Torold ezt a veletlenul beagyazott mappat, majd inditsd ujra a buildet.
  exit /b 1
)
where dotnet >nul 2>nul
if errorlevel 1 (
  echo HIBA: A .NET 8 SDK nem talalhato.
  exit /b 1
)

echo Avalonia AXAML kompatibilitasi ellenorzes...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0validate-axaml-compatibility.ps1"
if errorlevel 1 exit /b 1

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0prepare-embedded-assets.ps1"
if errorlevel 1 exit /b 1

if /I "%BUNDLE_RUNTIME_COMPONENTS%"=="true" (
  echo Bundled runtime komponensek elokeszitese...
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0prepare-bundled-runtime.ps1"
  if errorlevel 1 exit /b 1
) else (
  if exist "%~dp0EmbeddedAssets\BundledRuntime" rmdir /s /q "%~dp0EmbeddedAssets\BundledRuntime"
  echo Online runtime build: a JDK, Maven es MinGit nem kerul a bootstrapperbe.
)

dotnet build M2M.XmlEditor.Bootstrapper.csproj -c Release -p:BundleRuntimeComponents=%BUNDLE_RUNTIME_COMPONENTS%
set "BUILD_EXIT_CODE=%ERRORLEVEL%"
endlocal & exit /b %BUILD_EXIT_CODE%
