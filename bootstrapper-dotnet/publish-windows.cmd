@echo off
setlocal
cd /d "%~dp0"

set "BUNDLE_RUNTIME_COMPONENTS=false"
if /I "%~1"=="--bundle-runtime-components" set "BUNDLE_RUNTIME_COMPONENTS=true"
if /I "%~1"=="--online-runtime-components" set "BUNDLE_RUNTIME_COMPONENTS=false"
if not "%~2"=="" (
  echo HIBA: Ismeretlen publish parameter: %~2
  exit /b 1
)
if not "%~1"=="" if /I not "%~1"=="--bundle-runtime-components" if /I not "%~1"=="--online-runtime-components" (
  echo HIBA: Ismeretlen publish parameter: %~1
  echo Hasznalat: publish-windows.cmd [--bundle-runtime-components^|--online-runtime-components]
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

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0prepare-embedded-assets.ps1"
if errorlevel 1 exit /b 1

if /I "%BUNDLE_RUNTIME_COMPONENTS%"=="true" (
  echo Bundled runtime komponensek elokeszitese...
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0prepare-bundled-runtime.ps1"
  if errorlevel 1 exit /b 1
) else (
  if exist "%~dp0EmbeddedAssets\BundledRuntime" rmdir /s /q "%~dp0EmbeddedAssets\BundledRuntime"
  echo Online runtime publish: a JDK, Maven es MinGit nem kerul a bootstrapperbe.
)

if /I "%BUNDLE_RUNTIME_COMPONENTS%"=="true" (
  set "OUTPUT_EXE=M2M-XML-EDITOR-Embedded-Setup.exe"
) else (
  set "OUTPUT_EXE=M2M-XML-EDITOR-Online-Setup.exe"
)

set OUT=%~dp0..\dist\windows-x64
if exist "%OUT%" rmdir /s /q "%OUT%"
dotnet publish M2M.XmlEditor.Bootstrapper.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -p:CopyOutputSymbolsToPublishDirectory=false -p:BundleRuntimeComponents=%BUNDLE_RUNTIME_COMPONENTS% -o "%OUT%"
if errorlevel 1 exit /b 1
if exist "%OUT%\*.pdb" del /q "%OUT%\*.pdb"
for %%F in ("%OUT%\*") do (
  if /I not "%%~nxF"=="%OUTPUT_EXE%" (
    echo HIBA: A publish konyvtarban az EXE-n kivul mas fajl is maradt: %%~nxF
    exit /b 1
  )
)
for /D %%D in ("%OUT%\*") do (
  echo HIBA: A publish konyvtarban az EXE-n kivul konyvtar is maradt: %%~nxD
  exit /b 1
)
echo.
echo Egyfajlos Windows x64 bootstrapper: "%OUT%\%OUTPUT_EXE%"
if /I "%BUNDLE_RUNTIME_COMPONENTS%"=="true" (
  echo Runtime mod: BUNDLED - Temurin, Maven es MinGit beagyazva.
) else (
  echo Runtime mod: ONLINE - Temurin, Maven es MinGit szukseg eseten letoltodik.
)
echo A celgepen .NET runtime es kulso installer-project fajlok nem szuksegesek.
endlocal
