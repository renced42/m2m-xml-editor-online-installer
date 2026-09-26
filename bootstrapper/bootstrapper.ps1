#requires -version 5.1
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Windows PowerShell 5.1 kompatibilitas: a fajl UTF-8 BOM-mal kerul kiadasra.
# A konzol kodolasa a fejlesztoi .cmd inditasnal is UTF-8.
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ConfigPath = Join-Path $ScriptDir 'bootstrapper.config.json'
if (-not (Test-Path $ConfigPath)) {
    [System.Windows.Forms.MessageBox]::Show('Hiányzik a bootstrapper.config.json.', 'M2M XML EDITOR telepítő', 'OK', 'Error') | Out-Null
    exit 1
}
$Config = Get-Content -Raw -Encoding UTF8 $ConfigPath | ConvertFrom-Json
$InstallerProject = [System.IO.Path]::GetFullPath((Join-Path $ScriptDir $Config.installerProjectRelativePath))
$CacheRoot = Join-Path $env:LOCALAPPDATA $Config.cacheDirectoryName
$LogDir = Join-Path $CacheRoot 'logs'
$DownloadDir = Join-Path $CacheRoot 'downloads'
$RuntimeDir = Join-Path $CacheRoot 'runtime'
$WorkspaceDir = Join-Path $CacheRoot 'workspace'
New-Item -ItemType Directory -Force -Path $CacheRoot,$LogDir,$DownloadDir,$RuntimeDir,$WorkspaceDir | Out-Null
$LogFile = Join-Path $LogDir ("bootstrapper-{0}.log" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))

function Write-Log {
    param([string]$Message)
    $line = "{0} {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message
    Add-Content -Path $LogFile -Encoding UTF8 -Value $line
    if ($script:LogBox) {
        $script:LogBox.AppendText($Message + [Environment]::NewLine)
        $script:LogBox.SelectionStart = $script:LogBox.TextLength
        $script:LogBox.ScrollToCaret()
        [System.Windows.Forms.Application]::DoEvents()
    }
}

function Set-Status {
    param([string]$Text, [int]$Percent)
    $script:StatusLabel.Text = $Text
    $script:ProgressBar.Value = [Math]::Max(0, [Math]::Min(100, $Percent))
    [System.Windows.Forms.Application]::DoEvents()
}

function Format-ByteSize {
    param([long]$Bytes)
    if ($Bytes -ge 1GB) { return ("{0:N1} GB" -f ($Bytes / 1GB)) }
    if ($Bytes -ge 1MB) { return ("{0:N1} MB" -f ($Bytes / 1MB)) }
    if ($Bytes -ge 1KB) { return ("{0:N1} KB" -f ($Bytes / 1KB)) }
    return "$Bytes B"
}

function Set-DownloadProgress {
    param(
        [string]$Name,
        [long]$Downloaded,
        [long]$Total
    )

    if ($script:DownloadNameLabel) {
        $script:DownloadNameLabel.Text = "Letöltés: $Name"
    }

    if ($Total -gt 0) {
        $percent = [int][Math]::Min(100, [Math]::Floor(($Downloaded * 100.0) / $Total))
        if ($script:DownloadProgressBar) {
            $script:DownloadProgressBar.Style = 'Continuous'
            $script:DownloadProgressBar.Value = $percent
        }
        if ($script:DownloadDetailLabel) {
            $script:DownloadDetailLabel.Text = "$(Format-ByteSize $Downloaded) / $(Format-ByteSize $Total)  ($percent%)"
        }
    } else {
        if ($script:DownloadProgressBar) {
            $script:DownloadProgressBar.Style = 'Marquee'
        }
        if ($script:DownloadDetailLabel) {
            $script:DownloadDetailLabel.Text = "$(Format-ByteSize $Downloaded) letöltve"
        }
    }
    [System.Windows.Forms.Application]::DoEvents()
}

function Complete-DownloadProgress {
    param([string]$Name, [long]$Bytes)
    if ($script:DownloadProgressBar) {
        $script:DownloadProgressBar.Style = 'Continuous'
        $script:DownloadProgressBar.Value = 100
    }
    if ($script:DownloadNameLabel) {
        $script:DownloadNameLabel.Text = "Letöltve: $Name"
    }
    if ($script:DownloadDetailLabel) {
        $script:DownloadDetailLabel.Text = "$(Format-ByteSize $Bytes)  (100%)"
    }
    [System.Windows.Forms.Application]::DoEvents()
}

function Invoke-Download {
    param(
        [string]$Uri,
        [string]$OutFile,
        [string]$DisplayName = ''
    )

    if ([string]::IsNullOrWhiteSpace($DisplayName)) {
        $DisplayName = [System.IO.Path]::GetFileName($OutFile)
    }

    Write-Log "Letöltés: $Uri"
    if (Test-Path $OutFile) { Remove-Item -Force $OutFile }

    $old = [Net.ServicePointManager]::SecurityProtocol
    $response = $null
    $inputStream = $null
    $outputStream = $null
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $request = [System.Net.HttpWebRequest]::Create($Uri)
        $request.AllowAutoRedirect = $true
        $request.UserAgent = 'M2M-XML-EDITOR-Online-Setup/0.1'
        $request.Timeout = 120000
        $request.ReadWriteTimeout = 120000
        $response = $request.GetResponse()
        $total = [long]$response.ContentLength
        $inputStream = $response.GetResponseStream()
        $outputStream = New-Object System.IO.FileStream($OutFile, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        $buffer = New-Object byte[] 65536
        [long]$downloaded = 0
        Set-DownloadProgress -Name $DisplayName -Downloaded 0 -Total $total

        while (($read = $inputStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $outputStream.Write($buffer, 0, $read)
            $downloaded += $read
            Set-DownloadProgress -Name $DisplayName -Downloaded $downloaded -Total $total
        }
        $outputStream.Flush()
        Complete-DownloadProgress -Name $DisplayName -Bytes $downloaded
        Write-Log ("Letöltés kész: {0} ({1})" -f $DisplayName, (Format-ByteSize $downloaded))
    } finally {
        if ($outputStream) { $outputStream.Dispose() }
        if ($inputStream) { $inputStream.Dispose() }
        if ($response) { $response.Dispose() }
        [Net.ServicePointManager]::SecurityProtocol = $old
    }
}

function Set-ExtractionProgress {
    param(
        [string]$Name,
        [long]$Processed,
        [long]$Total,
        [string]$CurrentEntry = ''
    )

    if ($script:DownloadNameLabel) {
        $script:DownloadNameLabel.Text = "Kicsomagolás: $Name"
    }

    if ($Total -gt 0) {
        $percent = [int][Math]::Min(100, [Math]::Floor(($Processed * 100.0) / $Total))
        if ($script:DownloadProgressBar) {
            $script:DownloadProgressBar.Style = 'Continuous'
            $script:DownloadProgressBar.Value = $percent
        }
        if ($script:DownloadDetailLabel) {
            $detail = "$(Format-ByteSize $Processed) / $(Format-ByteSize $Total)  ($percent%)"
            if (-not [string]::IsNullOrWhiteSpace($CurrentEntry)) { $detail += "  -  $CurrentEntry" }
            $script:DownloadDetailLabel.Text = $detail
        }
    } else {
        if ($script:DownloadProgressBar) { $script:DownloadProgressBar.Style = 'Marquee' }
        if ($script:DownloadDetailLabel) { $script:DownloadDetailLabel.Text = $CurrentEntry }
    }
    [System.Windows.Forms.Application]::DoEvents()
}

function Complete-ExtractionProgress {
    param([string]$Name, [long]$Bytes)
    if ($script:DownloadProgressBar) {
        $script:DownloadProgressBar.Style = 'Continuous'
        $script:DownloadProgressBar.Value = 100
    }
    if ($script:DownloadNameLabel) { $script:DownloadNameLabel.Text = "Kicsomagolva: $Name" }
    if ($script:DownloadDetailLabel) { $script:DownloadDetailLabel.Text = "$(Format-ByteSize $Bytes)  (100%)" }
    [System.Windows.Forms.Application]::DoEvents()
}

function Expand-ZipClean {
    param(
        [string]$ZipFile,
        [string]$Destination,
        [string]$DisplayName = ''
    )

    if ([string]::IsNullOrWhiteSpace($DisplayName)) {
        $DisplayName = [System.IO.Path]::GetFileName($ZipFile)
    }

    Write-Log "Kicsomagolás: $DisplayName"
    if (Test-Path $Destination) { Remove-Item -Recurse -Force $Destination }
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipFile)
    try {
        [long]$total = 0
        foreach ($entry in $archive.Entries) { $total += [long]$entry.Length }
        [long]$processed = 0
        Set-ExtractionProgress -Name $DisplayName -Processed 0 -Total $total

        foreach ($entry in $archive.Entries) {
            $relativeName = $entry.FullName.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
            if ([string]::IsNullOrWhiteSpace($relativeName)) { continue }
            $target = Join-Path $Destination $relativeName
            $destinationRoot = [System.IO.Path]::GetFullPath($Destination).TrimEnd('\') + '\'
            $targetFullPath = [System.IO.Path]::GetFullPath($target)
            if (-not $targetFullPath.StartsWith($destinationRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Érvénytelen ZIP bejegyzés a célkönyvtáron kívül: $($entry.FullName)"
            }
            $target = $targetFullPath

            if ($entry.FullName.EndsWith('/')) {
                New-Item -ItemType Directory -Force -Path $target | Out-Null
                continue
            }

            $parent = Split-Path -Parent $target
            if (-not (Test-Path $parent)) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }

            $sourceStream = $null
            $targetStream = $null
            try {
                $sourceStream = $entry.Open()
                $targetStream = New-Object System.IO.FileStream($target, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
                $buffer = New-Object byte[] 65536
                while (($read = $sourceStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $targetStream.Write($buffer, 0, $read)
                    $processed += $read
                    Set-ExtractionProgress -Name $DisplayName -Processed $processed -Total $total -CurrentEntry $entry.FullName
                }
            } finally {
                if ($targetStream) { $targetStream.Dispose() }
                if ($sourceStream) { $sourceStream.Dispose() }
            }
        }
        Complete-ExtractionProgress -Name $DisplayName -Bytes $processed
        Write-Log ("Kicsomagolás kész: {0} ({1})" -f $DisplayName, (Format-ByteSize $processed))
    } finally {
        $archive.Dispose()
    }
}


function Test-ZipArchive {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
        $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
        try {
            return ($archive.Entries.Count -gt 0)
        } finally {
            $archive.Dispose()
        }
    } catch {
        return $false
    }
}

function Assert-ApplicationNotRunning {
    $applicationName = [string]$Config.applicationName
    $running = @()
    foreach ($process in @(Get-Process -ErrorAction SilentlyContinue)) {
        $matchesName = $false
        $matchesKnownPath = $false
        try { $matchesName = ($process.ProcessName -eq $applicationName) } catch { }
        try {
            if ($process.Path) {
                $full = [System.IO.Path]::GetFullPath($process.Path)
                $perUserRoot = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\M2M XML EDITOR')).TrimEnd('\\') + '\\'
                $machineRoot = if ($env:ProgramFiles) { [System.IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'M2M XML EDITOR')).TrimEnd('\\') + '\\' } else { '' }
                $matchesKnownPath = $full.StartsWith($perUserRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
                    ((-not [string]::IsNullOrWhiteSpace($machineRoot)) -and $full.StartsWith($machineRoot, [System.StringComparison]::OrdinalIgnoreCase))
            }
        } catch { }
        if ($matchesName -or $matchesKnownPath) { $running += $process }
    }
    if ($running.Count -gt 0) {
        $pids = (($running | ForEach-Object { $_.Id }) -join ', ')
        Write-Log "Futó M2M XML EDITOR példány észlelve. PID: $pids"
        throw 'Az M2M XML EDITOR valamelyik verziója jelenleg fut. Zárja be az alkalmazást, majd indítsa újra a telepítést/frissítést.'
    }
}

function ConvertTo-SemVer {
    param([string]$Version)
    if ([string]::IsNullOrWhiteSpace($Version)) { return $null }
    $value = $Version.Trim()
    $match = [regex]::Match($value, '^v?(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-(?<pre>[0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$')
    if (-not $match.Success) { return $null }
    return [pscustomobject]@{
        Original = $value
        Normalized = ("{0}.{1}.{2}{3}" -f $match.Groups['major'].Value, $match.Groups['minor'].Value, $match.Groups['patch'].Value, $(if ($match.Groups['pre'].Success) { '-' + $match.Groups['pre'].Value } else { '' }))
        Major = [int64]$match.Groups['major'].Value
        Minor = [int64]$match.Groups['minor'].Value
        Patch = [int64]$match.Groups['patch'].Value
        PreRelease = $(if ($match.Groups['pre'].Success) { $match.Groups['pre'].Value } else { $null })
    }
}

function Compare-SemVer {
    param([string]$Left, [string]$Right)
    $a = ConvertTo-SemVer $Left
    $b = ConvertTo-SemVer $Right
    if ($null -eq $a -or $null -eq $b) { throw "Nem összehasonlítható release-verzió: '$Left' / '$Right'." }
    foreach ($field in @('Major','Minor','Patch')) {
        if ($a.$field -lt $b.$field) { return -1 }
        if ($a.$field -gt $b.$field) { return 1 }
    }
    if ([string]::IsNullOrWhiteSpace($a.PreRelease) -and [string]::IsNullOrWhiteSpace($b.PreRelease)) { return 0 }
    if ([string]::IsNullOrWhiteSpace($a.PreRelease)) { return 1 }
    if ([string]::IsNullOrWhiteSpace($b.PreRelease)) { return -1 }
    $ai = @($a.PreRelease -split '\.')
    $bi = @($b.PreRelease -split '\.')
    $count = [Math]::Max($ai.Count, $bi.Count)
    for ($i = 0; $i -lt $count; $i++) {
        if ($i -ge $ai.Count) { return -1 }
        if ($i -ge $bi.Count) { return 1 }
        $av = $ai[$i]; $bv = $bi[$i]
        $an = $av -match '^\d+$'; $bn = $bv -match '^\d+$'
        if ($an -and $bn) {
            $avNum = [int64]$av; $bvNum = [int64]$bv
            if ($avNum -lt $bvNum) { return -1 }
            if ($avNum -gt $bvNum) { return 1 }
        } elseif ($an -and -not $bn) { return -1
        } elseif (-not $an -and $bn) { return 1
        } else {
            $cmp = [string]::CompareOrdinal($av, $bv)
            if ($cmp -lt 0) { return -1 }
            if ($cmp -gt 0) { return 1 }
        }
    }
    return 0
}

function Get-InstalledVersionInfos {
    $result = @()
    $roots = @()
    if ($env:LOCALAPPDATA) { $roots += [pscustomobject]@{ Scope='PerUser'; ProgramRoot=(Join-Path $env:LOCALAPPDATA 'Programs\M2M XML EDITOR'); DataRoot=(Join-Path $env:LOCALAPPDATA 'M2M-XML-EDITOR') } }
    if ($env:ProgramFiles -and $env:ProgramData) { $roots += [pscustomobject]@{ Scope='PerMachine'; ProgramRoot=(Join-Path $env:ProgramFiles 'M2M XML EDITOR'); DataRoot=(Join-Path $env:ProgramData 'M2M-XML-EDITOR') } }
    foreach ($root in $roots) {
        if (Test-Path -LiteralPath $root.ProgramRoot) {
            foreach ($dir in @(Get-ChildItem -LiteralPath $root.ProgramRoot -Directory -ErrorAction SilentlyContinue)) {
                $sem = ConvertTo-SemVer $dir.Name
                if ($sem -and (Test-Path -LiteralPath (Join-Path $dir.FullName 'M2M XML EDITOR.exe'))) {
                    $result += [pscustomobject]@{ Version=$sem.Normalized; Tag=$dir.Name; Scope=$root.Scope; ProgramDir=$dir.FullName; DataDir=(Join-Path $root.DataRoot $sem.Normalized); Legacy=$false }
                }
            }
        }
    }
    # R24 és korábbi per-user telepítés felismerése.
    $legacyKey = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\M2M XML EDITOR'
    if (Test-Path $legacyKey) {
        try {
            $legacy = Get-ItemProperty -LiteralPath $legacyKey
            $sem = ConvertTo-SemVer ([string]$legacy.DisplayVersion)
            if ($sem -and $legacy.InstallLocation) {
                $result += [pscustomobject]@{ Version=$sem.Normalized; Tag=$sem.Original; Scope='PerUser'; ProgramDir=[string]$legacy.InstallLocation; DataDir=(Join-Path $env:LOCALAPPDATA 'M2M-XML-EDITOR'); Legacy=$true }
            }
        } catch { Write-Log "FIGYELMEZTETÉS: legacy telepítés vizsgálata sikertelen: $($_.Exception.Message)" }
    }
    return @($result)
}

function Get-NewestInstalledVersionInfo {
    $items = @(Get-InstalledVersionInfos)
    $newest = $null
    foreach ($item in $items) {
        if ($null -eq $newest -or (Compare-SemVer $item.Version $newest.Version) -gt 0) { $newest = $item }
    }
    return $newest
}

function Get-DatabaseKindFromDataDirectory {
    param([string]$DataDirectory)
    if ([string]::IsNullOrWhiteSpace($DataDirectory) -or -not (Test-Path -LiteralPath $DataDirectory)) { return 'NONE' }
    $searchDirs = @($DataDirectory)
    $configDir = Join-Path $DataDirectory 'config'
    if (Test-Path -LiteralPath $configDir) { $searchDirs += $configDir }
    $files = @()
    foreach ($dir in $searchDirs) {
        $files += @(Get-ChildItem -LiteralPath $dir -File -ErrorAction SilentlyContinue | Where-Object {
            $_.Name -match '(?i)(application|config|setup|datasource).*\.(properties|ya?ml|conf|txt)$'
        })
    }
    foreach ($file in @($files | Select-Object -Unique)) {
        try {
            $content = Get-Content -LiteralPath $file.FullName -Raw -ErrorAction Stop
            if ($content -match '(?i)jdbc:h2:') { return 'H2' }
            if ($content -match '(?i)jdbc:(mysql|mariadb):') { return 'MYSQL' }
            if ($content -match '(?i)jdbc:postgresql:') { return 'POSTGRESQL' }
            if ($content -match '(?i)jdbc:oracle:') { return 'ORACLE' }
        } catch { }
    }
    return 'UNKNOWN'
}

function Copy-H2DataForNewVersion {
    param([object]$SourceInfo, [string]$TargetVersion)
    if ($null -eq $SourceInfo) { return }
    $kind = Get-DatabaseKindFromDataDirectory -DataDirectory $SourceInfo.DataDir
    if ($kind -ne 'H2') { throw "Az előző verzió adatbázisa nem H2 ($kind), ezért a konfiguráció/adatok automatikus másolása tiltott." }
    $targetRoot = if ($SourceInfo.Scope -eq 'PerMachine') { Join-Path $env:ProgramData 'M2M-XML-EDITOR' } else { Join-Path $env:LOCALAPPDATA 'M2M-XML-EDITOR' }
    $target = Join-Path $targetRoot $TargetVersion
    if (Test-Path -LiteralPath $target) { throw "A cél adatkönyvtár már létezik, ezért nem írható felül: $target" }
    New-Item -ItemType Directory -Force -Path $targetRoot | Out-Null
    Copy-Item -LiteralPath $SourceInfo.DataDir -Destination $target -Recurse -Force
    Write-Log "H2 konfiguráció és adatok másolva: $($SourceInfo.DataDir) -> $target"
}

function Invoke-GitHubApiJson {
    param([string]$Uri)
    $old = [Net.ServicePointManager]::SecurityProtocol
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $headers = @{ 'Accept'='application/vnd.github+json'; 'X-GitHub-Api-Version'='2026-03-10'; 'User-Agent'='M2M-XML-EDITOR-Online-Setup/0.2' }
        return Invoke-RestMethod -Method Get -Uri $Uri -Headers $headers -TimeoutSec 30
    } finally { [Net.ServicePointManager]::SecurityProtocol = $old }
}

function Get-GitHubReleaseCatalog {
    $cacheDir = Join-Path $CacheRoot 'cache'
    New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null
    $cacheFile = Join-Path $cacheDir 'releases.json'
    $base = "https://api.github.com/repos/$($Config.githubOwner)/$($Config.githubRepository)"
    try {
        Write-Log 'GitHub release lista lekérése...'
        $latest = Invoke-GitHubApiJson -Uri "$base/releases/latest"
        $releases = @(Invoke-GitHubApiJson -Uri "$base/releases?per_page=$($Config.githubReleasePageSize)")
        $cacheObject = [pscustomobject]@{ fetchedAt=(Get-Date).ToString('o'); latest=$latest; releases=$releases }
        $cacheObject | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $cacheFile -Encoding UTF8
        return $cacheObject
    } catch {
        Write-Log "FIGYELMEZTETÉS: GitHub API nem érhető el: $($_.Exception.Message)"
        if (Test-Path -LiteralPath $cacheFile) {
            Write-Log "Korábbi release cache használata: $cacheFile"
            return (Get-Content -LiteralPath $cacheFile -Raw -Encoding UTF8 | ConvertFrom-Json)
        }
        throw 'A GitHub release-lista nem kérdezhető le, és nincs használható helyi release-cache.'
    }
}

function Update-ReleaseCatalogUi {
    $script:ReleaseCombo.Items.Clear()
    $script:ReleaseByLabel = @{}
    $catalog = Get-GitHubReleaseCatalog
    $newest = Get-NewestInstalledVersionInfo

    $candidates = @()
    $latestTag = [string]$catalog.latest.tag_name
    $latestSem = ConvertTo-SemVer $latestTag
    if ($latestSem) {
        $candidates += [pscustomobject]@{ Label=("Latest stabil - {0}" -f $latestSem.Normalized); Tag=$latestTag; Version=$latestSem.Normalized; Prerelease=$false }
    }
    foreach ($release in @($catalog.releases | Where-Object { -not $_.draft -and $_.prerelease })) {
        $sem = ConvertTo-SemVer ([string]$release.tag_name)
        if (-not $sem) { continue }
        $candidates += [pscustomobject]@{ Label=("Prerelease - {0}" -f $sem.Normalized); Tag=[string]$release.tag_name; Version=$sem.Normalized; Prerelease=$true }
    }

    $seenVersions = @{}
    foreach ($candidate in $candidates) {
        if ($seenVersions.ContainsKey($candidate.Version)) { continue }
        if ($newest -and (Compare-SemVer $candidate.Version $newest.Version) -lt 0) {
            Write-Log "Régebbi release kihagyva a listából: $($candidate.Version) < $($newest.Version)"
            continue
        }
        $seenVersions[$candidate.Version] = $true
        [void]$script:ReleaseCombo.Items.Add($candidate.Label)
        $script:ReleaseByLabel[$candidate.Label] = [pscustomobject]@{ Tag=$candidate.Tag; Version=$candidate.Version; Prerelease=$candidate.Prerelease }
    }

    if ($script:ReleaseCombo.Items.Count -eq 0) {
        if ($newest) {
            throw "A GitHubon jelenleg nincs a telepített $($newest.Version) verzióval azonos vagy annál újabb választható stable/prerelease release."
        }
        throw 'Nem található telepíthető, szemantikus verziószámú GitHub Release.'
    }
    $script:ReleaseCombo.SelectedIndex = 0
    Update-InstalledAndCopyUi
}

function Update-InstalledAndCopyUi {
    $newest = Get-NewestInstalledVersionInfo
    $script:NewestInstalled = $newest
    if ($newest) {
        $db = Get-DatabaseKindFromDataDirectory -DataDirectory $newest.DataDir
        $legacyText = if ($newest.Legacy) { ' (korábbi telepítési struktúra)' } else { '' }
        $script:InstalledLabel.Text = "Legújabb telepített verzió: $($newest.Version)$legacyText | Adatbázis: $db"

        $selectedVersion = $null
        if ($script:ReleaseCombo.SelectedIndex -ge 0) {
            $selectedLabel = [string]$script:ReleaseCombo.SelectedItem
            $selectedRelease = $script:ReleaseByLabel[$selectedLabel]
            if ($selectedRelease) { $selectedVersion = [string]$selectedRelease.Version }
        }
        $isNewVersion = $selectedVersion -and ((Compare-SemVer $selectedVersion $newest.Version) -gt 0)
        $canClone = ($db -eq 'H2') -and $isNewVersion
        $script:CloneCheck.Enabled = $canClone
        if (-not $canClone) { $script:CloneCheck.Checked = $false }
        if ($db -eq 'H2' -and $isNewVersion) {
            $script:CloneCheck.Text = "Az $($newest.Version) H2 konfigurációjának és adatainak másolása a(z) $selectedVersion verzióhoz"
        } elseif ($db -eq 'H2') {
            $script:CloneCheck.Text = 'Azonos release újratelepítésekor a meglévő adat/config változatlanul megmarad'
        } else {
            $script:CloneCheck.Text = 'Konfiguráció/adatok másolása tiltott (csak H2 adatbázis esetén engedélyezett)'
        }
    } else {
        $script:InstalledLabel.Text = 'Telepített M2M XML EDITOR release nem található.'
        $script:CloneCheck.Enabled = $false
        $script:CloneCheck.Checked = $false
        $script:CloneCheck.Text = 'Konfiguráció/adatok másolása (nincs korábbi verzió)'
    }
}

function Get-SingleSubdirectory {
    param([string]$Parent)
    $dirs = @(Get-ChildItem -LiteralPath $Parent -Directory)
    if ($dirs.Count -eq 1) { return $dirs[0].FullName }
    return $Parent
}

function Ensure-Jdk17 {
    $javaHome = Join-Path $RuntimeDir 'jdk-17'
    $javaExe = Join-Path $javaHome 'bin\java.exe'
    $jpackageExe = Join-Path $javaHome 'bin\jpackage.exe'
    if ((Test-Path $javaExe) -and (Test-Path $jpackageExe)) {
        Write-Log "Java 17 cache használata: $javaHome"
        return $javaHome
    }

    Set-Status 'Java 17 előkészítése...' 10
    $zip = Join-Path $DownloadDir 'temurin-jdk17-windows-x64.zip'
    $metadataUrl = 'https://api.adoptium.net/v3/assets/latest/17/hotspot?architecture=x64&image_type=jdk&os=windows&vendor=eclipse'
    if (Test-ZipArchive -Path $zip) {
        Write-Log "Java 17 ZIP cache használata: $zip"
    } else {
        if (Test-Path $zip) { Remove-Item -Force $zip }
        Set-Status 'Java 17 letöltése...' 10
        Write-Log "Temurin JDK 17 metaadat lekérése: $metadataUrl"
        $metadata = Invoke-RestMethod -Method Get -Uri $metadataUrl -TimeoutSec 30
        if (-not $metadata -or $metadata.Count -eq 0 -or [string]::IsNullOrWhiteSpace([string]$metadata[0].binary.package.link)) {
            throw 'A Temurin JDK 17 metaadat-válasza nem tartalmaz közvetlen ZIP letöltési URL-t.'
        }
        $downloadUrl = [string]$metadata[0].binary.package.link
        Write-Log "Temurin JDK 17 közvetlen ZIP URL: $downloadUrl"
        Invoke-Download -Uri $downloadUrl -OutFile $zip -DisplayName 'Temurin JDK 17'
    }
    $tmp = Join-Path $RuntimeDir 'jdk-17-extract'
    Set-Status 'Java 17 kicsomagolása...' 15
    Expand-ZipClean -ZipFile $zip -Destination $tmp -DisplayName 'Temurin JDK 17'
    $actual = Get-SingleSubdirectory -Parent $tmp
    if (Test-Path $javaHome) { Remove-Item -Recurse -Force $javaHome }
    Move-Item -LiteralPath $actual -Destination $javaHome
    if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }
    if (-not (Test-Path $javaExe)) { throw 'A Java 17 letöltése/kibontása nem sikerült.' }
    Write-Log "Java 17 előkészítve: $javaHome"
    return $javaHome
}

function Ensure-Maven {
    $version = [string]$Config.mavenVersion
    $mavenHome = Join-Path $RuntimeDir ("apache-maven-$version")
    $mvnCmd = Join-Path $mavenHome 'bin\mvn.cmd'
    if (Test-Path $mvnCmd) {
        Write-Log "Maven cache használata: $mavenHome"
        return $mavenHome
    }
    Set-Status 'Maven előkészítése...' 20
    $zip = Join-Path $DownloadDir ("apache-maven-$version-bin.zip")
    $url = "https://archive.apache.org/dist/maven/maven-3/$version/binaries/apache-maven-$version-bin.zip"
    if (Test-ZipArchive -Path $zip) {
        Write-Log "Maven ZIP cache használata: $zip"
    } else {
        if (Test-Path $zip) { Remove-Item -Force $zip }
        Set-Status 'Maven letöltése...' 20
        Invoke-Download -Uri $url -OutFile $zip -DisplayName "Apache Maven $version"
    }
    Set-Status 'Maven kicsomagolása...' 25
    $tmp = Join-Path $RuntimeDir ("apache-maven-$version-extract")
    Expand-ZipClean -ZipFile $zip -Destination $tmp -DisplayName "Apache Maven $version"
    $actual = Get-SingleSubdirectory -Parent $tmp
    if (Test-Path $mavenHome) { Remove-Item -Recurse -Force $mavenHome }
    Move-Item -LiteralPath $actual -Destination $mavenHome
    if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }
    if (-not (Test-Path $mvnCmd)) { throw 'A Maven letöltése/kibontása nem sikerült.' }
    $javaHome = Join-Path $RuntimeDir 'jdk-17'
    $javaExe = Join-Path $javaHome 'bin\java.exe'
    if (-not (Test-Path $javaExe)) {
        throw "A Maven előkészítése közben eltűnt a bootstrapper Java 17 runtime-ja: $javaExe"
    }
    Write-Log "Maven előkészítve: $mavenHome"
    return $mavenHome
}

function ConvertTo-ProcessArgument {
    param([AllowEmptyString()][string]$Value)

    if ($null -eq $Value -or $Value.Length -eq 0) {
        return '""'
    }
    if ($Value -notmatch '[\s"]') {
        return $Value
    }

    # Windows CreateProcess parancssori idezojelezes. PowerShell 5.1 alatt a
    # ProcessStartInfo.ArgumentList meg nem erheto el, ezert .Arguments kell.
    $escaped = $Value -replace '(\\*)"', '$1$1\"'
    $escaped = $escaped -replace '(\\+)$', '$1$1'
    return '"' + $escaped + '"'
}

function Invoke-ProcessLogged {
    param(
        [string]$FilePath,
        [string[]]$Arguments,
        [string]$WorkingDirectory,
        [hashtable]$Environment = @{}
    )
    Write-Log ("Futtatás: {0} {1}" -f $FilePath, ($Arguments -join ' '))
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.WorkingDirectory = $WorkingDirectory
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    foreach ($key in $Environment.Keys) {
        $psi.EnvironmentVariables[$key] = [string]$Environment[$key]
    }

    $argumentText = (($Arguments | ForEach-Object { ConvertTo-ProcessArgument -Value ([string]$_) }) -join ' ')
    if ($FilePath -match '(?i)\.(cmd|bat)$') {
        $psi.FileName = $env:ComSpec
        $command = 'call ' + (ConvertTo-ProcessArgument -Value $FilePath)
        if (-not [string]::IsNullOrWhiteSpace($argumentText)) { $command += ' ' + $argumentText }
        $psi.Arguments = '/d /s /c ' + (ConvertTo-ProcessArgument -Value $command)
    } else {
        $psi.FileName = $FilePath
        $psi.Arguments = $argumentText
    }
    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi
    [void]$proc.Start()
    while (-not $proc.HasExited) {
        while (-not $proc.StandardOutput.EndOfStream) { Write-Log $proc.StandardOutput.ReadLine() }
        while (-not $proc.StandardError.EndOfStream) { Write-Log $proc.StandardError.ReadLine() }
        [System.Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 80
    }
    while (-not $proc.StandardOutput.EndOfStream) { Write-Log $proc.StandardOutput.ReadLine() }
    while (-not $proc.StandardError.EndOfStream) { Write-Log $proc.StandardError.ReadLine() }
    if ($proc.ExitCode -ne 0) { throw "A folyamat hibakóddal állt le: $($proc.ExitCode)" }
}

function Get-Source {
    param([string]$Ref)
    Set-Status 'GitHub forrás letöltése...' 30
    $safeRef = ($Ref -replace '[^A-Za-z0-9._-]', '_')
    $zip = Join-Path $DownloadDir ("source-$safeRef.zip")
    $url = "https://codeload.github.com/$($Config.githubOwner)/$($Config.githubRepository)/zip/refs/tags/$Ref"
    Invoke-Download -Uri $url -OutFile $zip -DisplayName "GitHub forrás ($Ref)"
    Set-Status 'GitHub forrás kicsomagolása...' 36
    $extract = Join-Path $WorkspaceDir 'source'
    Expand-ZipClean -ZipFile $zip -Destination $extract -DisplayName "GitHub forrás ($Ref)"
    $sourceRoot = Get-SingleSubdirectory -Parent $extract
    if (-not (Test-Path (Join-Path $sourceRoot 'pom.xml'))) { throw 'A letöltött forrás gyökerében nem található pom.xml.' }
    Write-Log "Forrás: $sourceRoot"
    return $sourceRoot
}

function Build-Application {
    param([string]$SourceRoot, [string]$JavaHome, [string]$MavenHome)
    Set-Status 'M2M XML EDITOR fordítása...' 45
    $mvn = Join-Path $MavenHome 'bin\mvn.cmd'
    $javaExe = Join-Path $JavaHome 'bin\java.exe'
    if (-not (Test-Path $javaExe)) { throw "A bootstrapper Java 17 runtime-ja nem található: $javaExe" }
    Write-Log "JAVA_HOME a Maven buildhez: $JavaHome"
    $args = @($Config.mavenBuildArguments -split '\s+')
    Invoke-ProcessLogged -FilePath $mvn -Arguments $args -WorkingDirectory $SourceRoot -Environment @{
        'JAVA_HOME' = $JavaHome
        'PATH' = "${JavaHome}\bin;${MavenHome}\bin;$env:PATH"
        'MAVEN_OPTS' = '-Dfile.encoding=UTF-8 -Dsun.stdout.encoding=UTF-8 -Dsun.stderr.encoding=UTF-8'
    }
}

function Find-ApplicationJar {
    param([string]$SourceRoot)
    $target = Join-Path $SourceRoot ([string]$Config.webModuleTarget)
    if (-not (Test-Path $target)) { throw "Nem található a web modul target könyvtára: $target" }
    $jars = @(Get-ChildItem -Path $target -Filter '*.jar' -File | Where-Object {
        $_.Name -notmatch '(^original-|sources|javadoc)'
    } | Sort-Object LastWriteTime -Descending)
    if ($jars.Count -eq 0) { throw "Nem található futtatható JAR: $target" }
    Write-Log "Alkalmazás JAR: $($jars[0].FullName)"
    return $jars[0].FullName
}

function Build-AppImage {
    param([string]$SourceRoot, [string]$JarFile, [string]$JavaHome, [string]$Version, [string]$DataDir)
    Set-Status 'Futtatható alkalmazáscsomag készítése...' 60
    $stage = Join-Path $WorkspaceDir 'app-image-stage'
    $input = Join-Path $stage 'input'
    $dest = Join-Path $stage 'output'
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
    New-Item -ItemType Directory -Force -Path $input,$dest | Out-Null
    Copy-Item -LiteralPath $JarFile -Destination (Join-Path $input 'app.jar')
    foreach ($name in @('config','licenses')) {
        $p = Join-Path $SourceRoot $name
        if (Test-Path $p) { Copy-Item -Recurse -Force $p (Join-Path $input $name) }
    }
    foreach ($name in @('README.md','LICENSE','README-LICENC.md')) {
        $p = Join-Path $SourceRoot $name
        if (Test-Path $p) { Copy-Item -Force $p $input }
    }
    $jpackage = Join-Path $JavaHome 'bin\jpackage.exe'
    $args = @('--type','app-image','--name',[string]$Config.applicationName,'--dest',$dest,'--input',$input,'--main-jar','app.jar','--main-class',[string]$Config.mainClass,'--java-options','-Dfile.encoding=UTF-8','--java-options',("-Dapp.data.dir={0}" -f $DataDir),'--app-version',((ConvertTo-SemVer $Version).Major.ToString() + '.' + (ConvertTo-SemVer $Version).Minor.ToString() + '.' + (ConvertTo-SemVer $Version).Patch.ToString()))
    $icon = Join-Path $SourceRoot 'installer\windows\app.ico'
    if (-not (Test-Path $icon)) {
        $icon = Join-Path $InstallerProject 'src\main\izpack\resources\app.ico'
    }
    if (Test-Path $icon) {
        $args += @('--icon',$icon)
        Write-Log "Alkalmazásikon: $icon"
    } else {
        Write-Log 'FIGYELMEZTETÉS: Egyedi alkalmazásikon nem található; a jpackage alapértelmezett ikont használja.'
    }
    Invoke-ProcessLogged -FilePath $jpackage -Arguments $args -WorkingDirectory $SourceRoot
    $image = Join-Path $dest ([string]$Config.applicationName)
    if (-not (Test-Path $image)) { throw "A jpackage app-image nem jött létre: $image" }
    Write-Log "App-image: $image"
    return $image
}

function Stage-InstallerPayload {
    param([string]$AppImage)
    Set-Status 'Telepítőcsomag előkészítése...' 72
    $payload = Join-Path $InstallerProject 'src\main\izpack\payload'
    if (Test-Path $payload) { Remove-Item -Recurse -Force $payload }
    New-Item -ItemType Directory -Force -Path $payload | Out-Null
    Copy-Item -Path (Join-Path $AppImage '*') -Destination $payload -Recurse -Force
    $installerIcon = Join-Path $InstallerProject 'src\main\izpack\resources\app.ico'
    if (Test-Path $installerIcon) {
        Copy-Item -LiteralPath $installerIcon -Destination (Join-Path $payload 'app.ico') -Force
    }
    Write-Log "IzPack payload előkészítve: $payload"
}

function Build-IzPackInstaller {
    param([string]$JavaHome, [string]$MavenHome, [string]$Version)
    Set-Status 'IzPack telepítő fordítása...' 82
    $mvn = Join-Path $MavenHome 'bin\mvn.cmd'
    $javaExe = Join-Path $JavaHome 'bin\java.exe'
    if (-not (Test-Path $javaExe)) { throw "A bootstrapper Java 17 runtime-ja nem található: $javaExe" }
    Write-Log "JAVA_HOME az IzPack buildhez: $JavaHome"
    Invoke-ProcessLogged -FilePath $mvn -Arguments @('clean','package',("-Dinstaller.app.version={0}" -f $Version)) -WorkingDirectory $InstallerProject -Environment @{
        'JAVA_HOME' = $JavaHome
        'PATH' = "${JavaHome}\bin;${MavenHome}\bin;$env:PATH"
        'MAVEN_OPTS' = '-Dfile.encoding=UTF-8 -Dsun.stdout.encoding=UTF-8 -Dsun.stderr.encoding=UTF-8'
    }
    $target = Join-Path $InstallerProject 'target'
    $result = Get-ChildItem -Path $target -Filter ([string]$Config.installerOutputPattern) -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $result) { throw 'Az IzPack installer JAR nem található.' }
    Write-Log "Installer: $($result.FullName)"
    return $result.FullName
}

function Start-Installer {
    param([string]$JavaHome, [string]$InstallerJar)
    Set-Status 'Telepítő indítása...' 100
    $javaw = Join-Path $JavaHome 'bin\javaw.exe'
    if (-not (Test-Path $javaw)) { $javaw = Join-Path $JavaHome 'bin\java.exe' }
    Start-Process -FilePath $javaw -ArgumentList @('-Dfile.encoding=UTF-8','-jar',$InstallerJar) -WorkingDirectory (Split-Path -Parent $InstallerJar)
    Write-Log 'Az IzPack GUI elindult.'
}

function Run-Bootstrap {
    $script:StartButton.Enabled = $false
    try {
        if ($script:ReleaseCombo.SelectedIndex -lt 0) { throw 'Válasszon telepítendő GitHub release-t.' }
        $label = [string]$script:ReleaseCombo.SelectedItem
        $release = $script:ReleaseByLabel[$label]
        if ($null -eq $release) { throw 'A kiválasztott GitHub release nem azonosítható.' }
        $ref = [string]$release.Tag
        $version = [string]$release.Version
        $newest = Get-NewestInstalledVersionInfo
        if ($newest -and (Compare-SemVer $version $newest.Version) -lt 0) {
            throw "A kiválasztott $version release régebbi a legújabb telepített $($newest.Version) verziónál. Régebbi release telepítése nem engedélyezett."
        }

        Set-Status 'Előkészítés...' 2
        Write-Log "Bootstrap indult. GitHub release: $ref; alkalmazásverzió: $version"
        Assert-ApplicationNotRunning

        $dataRoot = Join-Path $env:LOCALAPPDATA 'M2M-XML-EDITOR'
        $dataDir = Join-Path $dataRoot $version
        $sameVersionDataExists = Test-Path -LiteralPath $dataDir
        if ($script:CloneCheck.Checked) {
            if ($sameVersionDataExists) { throw "A(z) $version adatkönyvtára már létezik; meglévő release újratelepítésekor az adatok nem írhatók felül másik verzió adataival." }
            Copy-H2DataForNewVersion -SourceInfo $newest -TargetVersion $version
        } elseif (-not $sameVersionDataExists) {
            New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
            Write-Log "Új verzió-adatkönyvtár előkészítve: $dataDir"
        }

        $jdk = Ensure-Jdk17
        $maven = Ensure-Maven
        $source = Get-Source -Ref $ref
        Build-Application -SourceRoot $source -JavaHome $jdk -MavenHome $maven
        $jar = Find-ApplicationJar -SourceRoot $source
        $image = Build-AppImage -SourceRoot $source -JarFile $jar -JavaHome $jdk -Version $version -DataDir $dataDir
        Stage-InstallerPayload -AppImage $image
        $installer = Build-IzPackInstaller -JavaHome $jdk -MavenHome $maven -Version $version
        Assert-ApplicationNotRunning
        Start-Installer -JavaHome $jdk -InstallerJar $installer
        $script:StatusLabel.Text = "A(z) $version grafikus telepítője elindult."
        Write-Log 'A bootstrapper sikeresen befejeződött; az ablak bezáródik.'
        $script:BootstrapSucceeded = $true
        $form.BeginInvoke([Action]{ $form.Close() }) | Out-Null
    } catch {
        Write-Log ("HIBA: " + $_.Exception.Message)
        $script:StatusLabel.Text = 'Hiba történt.'
        [System.Windows.Forms.MessageBox]::Show($_.Exception.Message + "`r`n`r`nNapló: $LogFile", 'M2M XML EDITOR telepítő', 'OK', 'Error') | Out-Null
    } finally {
        $script:StartButton.Enabled = $true
    }
}

$script:BootstrapSucceeded = $false

$form = New-Object System.Windows.Forms.Form
$form.Text = 'M2M XML EDITOR – Online telepítő'
$form.StartPosition = 'CenterScreen'
$form.Size = New-Object System.Drawing.Size(900, 780)
$form.MinimumSize = New-Object System.Drawing.Size(820, 740)
$form.Font = New-Object System.Drawing.Font('Segoe UI', 11)

$title = New-Object System.Windows.Forms.Label
$title.Text = 'M2M XML EDITOR – forrásból történő telepítés'
$title.Font = New-Object System.Drawing.Font('Segoe UI Semibold', 18)
$title.AutoSize = $true
$title.Location = New-Object System.Drawing.Point(28, 24)
$form.Controls.Add($title)

$subtitle = New-Object System.Windows.Forms.Label
$subtitle.Text = 'A telepítő saját Java 17 és Maven környezetet használ. A rendszer Java/Maven telepítését nem módosítja.'
$subtitle.AutoSize = $true
$subtitle.Location = New-Object System.Drawing.Point(31, 66)
$form.Controls.Add($subtitle)

$repoLabel = New-Object System.Windows.Forms.Label
$repoLabel.Text = 'Forrás:'
$repoLabel.AutoSize = $true
$repoLabel.Location = New-Object System.Drawing.Point(31, 112)
$form.Controls.Add($repoLabel)

$repoValue = New-Object System.Windows.Forms.Label
$repoValue.Text = "https://github.com/$($Config.githubOwner)/$($Config.githubRepository)"
$repoValue.AutoSize = $true
$repoValue.Location = New-Object System.Drawing.Point(150, 112)
$form.Controls.Add($repoValue)

$releaseLabel = New-Object System.Windows.Forms.Label
$releaseLabel.Text = 'Telepítendő release:'
$releaseLabel.AutoSize = $true
$releaseLabel.Location = New-Object System.Drawing.Point(31, 148)
$form.Controls.Add($releaseLabel)

$script:ReleaseByLabel = @{}
$script:ReleaseCombo = New-Object System.Windows.Forms.ComboBox
$script:ReleaseCombo.DropDownStyle = 'DropDownList'
$script:ReleaseCombo.Location = New-Object System.Drawing.Point(190, 144)
$script:ReleaseCombo.Width = 330
$script:ReleaseCombo.Add_SelectedIndexChanged({ Update-InstalledAndCopyUi })
$form.Controls.Add($script:ReleaseCombo)

$refreshReleases = New-Object System.Windows.Forms.Button
$refreshReleases.Text = 'Release-ek frissítése'
$refreshReleases.Location = New-Object System.Drawing.Point(535, 142)
$refreshReleases.Size = New-Object System.Drawing.Size(180, 34)
$refreshReleases.Add_Click({
    try { Update-ReleaseCatalogUi } catch { [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, 'GitHub release lista', 'OK', 'Error') | Out-Null }
})
$form.Controls.Add($refreshReleases)

$script:InstalledLabel = New-Object System.Windows.Forms.Label
$script:InstalledLabel.Text = 'Telepített verziók vizsgálata...'
$script:InstalledLabel.AutoSize = $true
$script:InstalledLabel.Location = New-Object System.Drawing.Point(31, 190)
$form.Controls.Add($script:InstalledLabel)

$script:CloneCheck = New-Object System.Windows.Forms.CheckBox
$script:CloneCheck.Text = 'Konfiguráció/adatok másolása (csak H2 adatbázis esetén)'
$script:CloneCheck.AutoSize = $true
$script:CloneCheck.Enabled = $false
$script:CloneCheck.Location = New-Object System.Drawing.Point(34, 222)
$form.Controls.Add($script:CloneCheck)

$script:StartButton = New-Object System.Windows.Forms.Button
$script:StartButton.Text = 'Kiválasztott release telepítése'
$script:StartButton.Location = New-Object System.Drawing.Point(560, 215)
$script:StartButton.Size = New-Object System.Drawing.Size(275, 38)
$script:StartButton.Add_Click({ Run-Bootstrap })
$form.Controls.Add($script:StartButton)

$script:ProgressBar = New-Object System.Windows.Forms.ProgressBar
$script:ProgressBar.Location = New-Object System.Drawing.Point(34, 275)
$script:ProgressBar.Size = New-Object System.Drawing.Size(801, 24)
$form.Controls.Add($script:ProgressBar)

$script:StatusLabel = New-Object System.Windows.Forms.Label
$script:StatusLabel.Text = 'Indításra kész.'
$script:StatusLabel.AutoSize = $true
$script:StatusLabel.Location = New-Object System.Drawing.Point(31, 311)
$form.Controls.Add($script:StatusLabel)

$downloadHeader = New-Object System.Windows.Forms.Label
$downloadHeader.Text = 'Aktuális művelet:'
$downloadHeader.AutoSize = $true
$downloadHeader.Font = New-Object System.Drawing.Font('Segoe UI Semibold', 10.5)
$downloadHeader.Location = New-Object System.Drawing.Point(31, 345)
$form.Controls.Add($downloadHeader)

$script:DownloadNameLabel = New-Object System.Windows.Forms.Label
$script:DownloadNameLabel.Text = 'Nincs folyamatban letöltés vagy kicsomagolás.'
$script:DownloadNameLabel.AutoSize = $true
$script:DownloadNameLabel.Location = New-Object System.Drawing.Point(31, 371)
$form.Controls.Add($script:DownloadNameLabel)

$script:DownloadProgressBar = New-Object System.Windows.Forms.ProgressBar
$script:DownloadProgressBar.Location = New-Object System.Drawing.Point(34, 397)
$script:DownloadProgressBar.Size = New-Object System.Drawing.Size(801, 22)
$script:DownloadProgressBar.Style = 'Continuous'
$form.Controls.Add($script:DownloadProgressBar)

$script:DownloadDetailLabel = New-Object System.Windows.Forms.Label
$script:DownloadDetailLabel.Text = ''
$script:DownloadDetailLabel.AutoSize = $true
$script:DownloadDetailLabel.Location = New-Object System.Drawing.Point(31, 427)
$form.Controls.Add($script:DownloadDetailLabel)

$logLabel = New-Object System.Windows.Forms.Label
$logLabel.Text = 'Folyamatnapló:'
$logLabel.AutoSize = $true
$logLabel.Location = New-Object System.Drawing.Point(31, 459)
$form.Controls.Add($logLabel)

$script:LogBox = New-Object System.Windows.Forms.TextBox
$script:LogBox.Location = New-Object System.Drawing.Point(34, 488)
$script:LogBox.Size = New-Object System.Drawing.Size(801, 160)
$script:LogBox.Multiline = $true
$script:LogBox.ReadOnly = $true
$script:LogBox.ScrollBars = 'Vertical'
$script:LogBox.Font = New-Object System.Drawing.Font('Consolas', 9.5)
$form.Controls.Add($script:LogBox)

$cacheLabel = New-Object System.Windows.Forms.Label
$cacheLabel.Text = "Cache: $CacheRoot"
$cacheLabel.AutoSize = $true
$cacheLabel.Location = New-Object System.Drawing.Point(31, 665)
$form.Controls.Add($cacheLabel)

$form.Add_Shown({
    try {
        Update-InstalledAndCopyUi
        Update-ReleaseCatalogUi
    } catch {
        Write-Log ("HIBA a release-lista betöltésekor: " + $_.Exception.Message)
        [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, 'GitHub release lista', 'OK', 'Error') | Out-Null
    }
})

[void]$form.ShowDialog()
