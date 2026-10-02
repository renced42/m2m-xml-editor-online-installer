param(
    [string]$CacheDirectory = ""
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$projectDir = $PSScriptRoot
$configPath = Join-Path $projectDir 'bootstrapper.config.json'
$assetRoot = Join-Path $projectDir 'EmbeddedAssets\BundledRuntime'
if ([string]::IsNullOrWhiteSpace($CacheDirectory)) {
    $CacheDirectory = Join-Path $projectDir '.build\runtime-cache'
}

function Ensure-Directory([string]$Path) {
    if (-not (Test-Path $Path)) { New-Item -ItemType Directory -Path $Path -Force | Out-Null }
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -Algorithm SHA256 -Path $Path).Hash.ToLowerInvariant()
}

function New-BuildWebClient() {
    $client = New-Object System.Net.WebClient
    $client.Headers['User-Agent'] = 'M2M-XML-EDITOR-Online-Setup-Build/0.9.30'
    $client.Proxy = [System.Net.WebRequest]::DefaultWebProxy
    if ($client.Proxy) {
        $client.Proxy.Credentials = [System.Net.CredentialCache]::DefaultCredentials
    }
    return $client
}

function Test-ValidZip([string]$Path) {
    if (-not (Test-Path $Path)) { return $false }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    try {
        $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
        $archive.Dispose()
        return $true
    }
    catch {
        return $false
    }
}

function Import-ExistingRuntimeCache([string]$SourceFile, [string]$CacheFile, [string]$DisplayName, [bool]$RequireZip) {
    if (Test-Path $CacheFile) { return $false }
    if (-not (Test-Path $SourceFile)) { return $false }
    if ($RequireZip -and -not (Test-ValidZip $SourceFile)) {
        Write-Host "Korabbi runtime cache figyelmen kivul hagyva, mert nem ervenyes ZIP: $SourceFile"
        return $false
    }
    Ensure-Directory (Split-Path -Parent $CacheFile)
    Copy-Item $SourceFile $CacheFile -Force
    Write-Host "Korabbi bootstrapper download-cache atveve: $DisplayName -> $CacheFile"
    return $true
}

function Invoke-CachedDownload([string]$Uri, [string]$CacheFile, [string]$DisplayName, [bool]$RequireZip) {
    Ensure-Directory (Split-Path -Parent $CacheFile)
    if (Test-Path $CacheFile) {
        if (-not $RequireZip -or (Test-ValidZip $CacheFile)) {
            Write-Host "Build cache hasznalata: $DisplayName -> $CacheFile"
            return
        }
        Write-Host "Sertult build-cache torlese: $CacheFile"
        Remove-Item -Force $CacheFile
    }

    $partial = $CacheFile + '.part'
    if (Test-Path $partial) { Remove-Item -Force $partial }
    Write-Host "Letoltes buildhez: $DisplayName"
    Write-Host "  $Uri"
    try {
        $webClient = New-BuildWebClient
        $webClient.DownloadFile($Uri, $partial)
        if ($RequireZip -and -not (Test-ValidZip $partial)) {
            throw "A letoltott $DisplayName nem ervenyes ZIP. A halozati proxy vagy tuzfal valoszinuleg nem a kert binaris tartalmat adta vissza."
        }
        Move-Item -Force $partial $CacheFile
    }
    finally {
        if (Test-Path $partial) { Remove-Item -Force $partial }
        if ($webClient) { $webClient.Dispose() }
    }
}

function Resolve-TemurinUrl([int]$MajorVersion) {
    $metadataUrl = "https://api.adoptium.net/v3/assets/latest/$MajorVersion/hotspot?architecture=x64&image_type=jdk&os=windows&vendor=eclipse"
    Write-Host "Temurin metaadat lekerese: $metadataUrl"
    try {
        $webClient = New-BuildWebClient
        $json = $webClient.DownloadString($metadataUrl)
        $response = $json | ConvertFrom-Json
    }
    catch {
        throw "A Temurin $MajorVersion metaadatainak lekerese nem sikerult. Ellenorizd a Windows rendszerproxy/tuzfal hitelesitest, vagy helyezd el a Temurin ZIP-et a build-cache-ben. Reszlet: $($_.Exception.Message)"
    }
    finally {
        if ($webClient) { $webClient.Dispose() }
    }
    if (-not $response -or -not $response[0].binary.package.link) {
        throw "A Temurin $MajorVersion metaadat-valasz nem tartalmaz kozvetlen ZIP URL-t. Proxy vagy tuzfal autentikacios oldal is okozhatja ezt."
    }
    return [string]$response[0].binary.package.link
}

function Copy-LicenseFilesFromZip([string]$ZipPath, [string]$Destination, [string[]]$Names) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path $Destination) { Remove-Item -Recurse -Force $Destination }
    Ensure-Directory $Destination
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        foreach ($entry in $archive.Entries) {
            if ([string]::IsNullOrWhiteSpace($entry.Name)) { continue }
            foreach ($name in $Names) {
                if ($entry.FullName -match $name) {
                    $safeName = ($entry.FullName -replace '[\\/:*?"<>|]', '_')
                    [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $Destination $safeName), $true)
                    break
                }
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

$config = Get-Content -Raw -Encoding UTF8 $configPath | ConvertFrom-Json
Ensure-Directory $CacheDirectory
if (Test-Path $assetRoot) { Remove-Item -Recurse -Force $assetRoot }
Ensure-Directory $assetRoot

$jdkCache = Join-Path $CacheDirectory ("temurin-jdk-{0}-windows-x64.zip" -f $config.javaMajorVersion)
$mavenCache = Join-Path $CacheDirectory ("apache-maven-{0}-bin.zip" -f $config.mavenVersion)
$gitCache = Join-Path $CacheDirectory ("MinGit-{0}-64-bit.zip" -f $config.gitMinVersion)

$existingDownloads = $null
if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
    $existingDownloads = Join-Path (Join-Path $env:LOCALAPPDATA ([string]$config.cacheDirectoryName)) 'downloads'
}
if ($existingDownloads) {
    Import-ExistingRuntimeCache (Join-Path $existingDownloads ("temurin-jdk-{0}-windows-x64.zip" -f $config.javaMajorVersion)) $jdkCache ("Eclipse Temurin JDK {0}" -f $config.javaMajorVersion) $true | Out-Null
    Import-ExistingRuntimeCache (Join-Path $existingDownloads ("apache-maven-{0}-bin.zip" -f $config.mavenVersion)) $mavenCache ("Apache Maven {0}" -f $config.mavenVersion) $true | Out-Null
    Import-ExistingRuntimeCache (Join-Path $existingDownloads ("MinGit-{0}-64-bit.zip" -f $config.gitMinVersion)) $gitCache ("MinGit {0}" -f $config.gitMinVersion) $true | Out-Null
}

$temurinUrl = 'cache'
if (-not (Test-ValidZip $jdkCache)) {
    if (Test-Path $jdkCache) { Remove-Item -Force $jdkCache }
    $temurinUrl = Resolve-TemurinUrl ([int]$config.javaMajorVersion)
    Invoke-CachedDownload $temurinUrl $jdkCache ("Eclipse Temurin JDK {0}" -f $config.javaMajorVersion) $true
}
else {
    Write-Host "Build cache hasznalata: Eclipse Temurin JDK $($config.javaMajorVersion) -> $jdkCache"
}

$mavenUrl = ("https://archive.apache.org/dist/maven/maven-3/{0}/binaries/apache-maven-{0}-bin.zip" -f $config.mavenVersion)
Invoke-CachedDownload $mavenUrl $mavenCache ("Apache Maven {0}" -f $config.mavenVersion) $true
Invoke-CachedDownload ([string]$config.gitMinDownloadUrl) $gitCache ("MinGit {0}" -f $config.gitMinVersion) $true

$gitSha = Get-Sha256 $gitCache
if (-not [string]::IsNullOrWhiteSpace([string]$config.gitMinSha256) -and $gitSha -ne ([string]$config.gitMinSha256).ToLowerInvariant()) {
    throw "A MinGit build-cache SHA-256 ellenorzese sikertelen. Elvart: $($config.gitMinSha256); tenyleges: $gitSha"
}

Copy-Item $jdkCache (Join-Path $assetRoot 'temurin-jdk.zip') -Force
Copy-Item $mavenCache (Join-Path $assetRoot 'apache-maven.zip') -Force
Copy-Item $gitCache (Join-Path $assetRoot 'mingit.zip') -Force

$licensesRoot = Join-Path $assetRoot 'licenses'
Copy-LicenseFilesFromZip $jdkCache (Join-Path $licensesRoot 'temurin') @('(^|/)(LICENSE|NOTICE)(\.|$)', '(^|/)legal/')
Copy-LicenseFilesFromZip $mavenCache (Join-Path $licensesRoot 'maven') @('(^|/)(LICENSE|NOTICE)(\.|$)')

$gitLicenseTarget = Join-Path $licensesRoot 'git-for-windows'
Copy-LicenseFilesFromZip $gitCache $gitLicenseTarget @('(^|/)(LICENSE|COPYING|NOTICE)(\.|$)', '(^|/)mingw64/share/licenses/')
$gitLicenseCount = @(Get-ChildItem $gitLicenseTarget -File -Recurse).Count
if ($gitLicenseCount -eq 0) { throw 'A MinGit csomagban nem talalhato a tovabbterjeszteshez megorzendo licencanyag.' }

$temurinLicenseCount = @(Get-ChildItem (Join-Path $licensesRoot 'temurin') -File -Recurse).Count
$mavenLicenseCount = @(Get-ChildItem (Join-Path $licensesRoot 'maven') -File -Recurse).Count
if ($temurinLicenseCount -eq 0) { throw 'A Temurin csomagbol nem sikerult licenc/legal fajlokat kinyerni.' }
if ($mavenLicenseCount -eq 0) { throw 'A Maven csomagbol nem sikerult LICENSE/NOTICE fajlokat kinyerni.' }

$manifest = [ordered]@{
    formatVersion = 1
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    javaMajorVersion = [int]$config.javaMajorVersion
    mavenVersion = [string]$config.mavenVersion
    gitMinVersion = [string]$config.gitMinVersion
    packages = [ordered]@{
        jdk = [ordered]@{ file = 'temurin-jdk.zip'; sha256 = (Get-Sha256 (Join-Path $assetRoot 'temurin-jdk.zip')); source = $temurinUrl }
        maven = [ordered]@{ file = 'apache-maven.zip'; sha256 = (Get-Sha256 (Join-Path $assetRoot 'apache-maven.zip')); source = $mavenUrl }
        git = [ordered]@{ file = 'mingit.zip'; sha256 = (Get-Sha256 (Join-Path $assetRoot 'mingit.zip')); source = [string]$config.gitMinDownloadUrl }
    }
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -Path (Join-Path $assetRoot 'manifest.json') -Encoding UTF8

$notice = @"
M2M XML EDITOR Online Setup - third-party bundled runtime components

Eclipse Temurin JDK $($config.javaMajorVersion)
Apache Maven $($config.mavenVersion)
Git for Windows MinGit $($config.gitMinVersion)

A teljes, a csomagokbol kinyert licenc- es jogi anyag az EmbeddedAssets/BundledRuntime/licenses konyvtarban talalhato, es a bundled bootstrapperbe beagyazasra kerul.
"@
$notice | Set-Content -Path (Join-Path $assetRoot 'THIRD-PARTY-NOTICES.txt') -Encoding UTF8
$licensesZip = Join-Path $assetRoot 'third-party-licenses.zip'
if (Test-Path $licensesZip) { Remove-Item -Force $licensesZip }
Compress-Archive -Path (Join-Path $licensesRoot '*') -DestinationPath $licensesZip -CompressionLevel Optimal -Force
Write-Host "Third-party licencanyag elokeszitve:"
Write-Host "  Temurin: $temurinLicenseCount fajl"
Write-Host "  Maven: $mavenLicenseCount fajl"
Write-Host "  MinGit/Git for Windows: $gitLicenseCount fajl"
Write-Host "  Notice: $(Join-Path $assetRoot 'THIRD-PARTY-NOTICES.txt')"
Write-Host "  Licencarchivum: $licensesZip"
Write-Host "Bundled runtime assetek elokeszitve: $assetRoot"
