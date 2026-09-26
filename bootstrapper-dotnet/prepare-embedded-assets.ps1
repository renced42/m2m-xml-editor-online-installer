param()

$ErrorActionPreference = 'Stop'
$projectDir = $PSScriptRoot
$rootDir = Split-Path -Parent $projectDir
$outDir = Join-Path $projectDir 'EmbeddedAssets'
$outZip = Join-Path $outDir 'installer-project.zip'
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('m2m-bootstrapper-assets-' + [Guid]::NewGuid().ToString('N'))

try {
    if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
    if (Test-Path $outZip) { Remove-Item -Force $outZip }
    New-Item -ItemType Directory -Path $tempRoot | Out-Null

    Copy-Item (Join-Path $rootDir 'pom.xml') (Join-Path $tempRoot 'pom.xml') -Force

    $mvnDir = Join-Path $tempRoot '.mvn'
    New-Item -ItemType Directory -Path $mvnDir | Out-Null
    Copy-Item (Join-Path $rootDir '.mvn\jvm.config') (Join-Path $mvnDir 'jvm.config') -Force

    $izpackTarget = Join-Path $tempRoot 'src\main\izpack'
    New-Item -ItemType Directory -Path (Split-Path -Parent $izpackTarget) -Force | Out-Null
    Copy-Item (Join-Path $rootDir 'src\main\izpack') $izpackTarget -Recurse -Force

    Compress-Archive -Path (Join-Path $tempRoot '*') -DestinationPath $outZip -CompressionLevel Optimal -Force
    Write-Host "Embedded installer projekt elkeszult: $outZip"
}
finally {
    if (Test-Path $tempRoot) { Remove-Item -Recurse -Force $tempRoot }
}
