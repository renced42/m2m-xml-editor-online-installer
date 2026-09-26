param(
    [Parameter(Mandatory = $true)]
    [string]$InstallPath,

    [Parameter(Mandatory = $true)]
    [string]$AppVersion,

    [Parameter(Mandatory = $true)]
    [ValidateSet('CurrentUser', 'AllUsers')]
    [string]$InstallScope,

    [switch]$FromTemp
)

$ErrorActionPreference = 'Stop'
$script:UninstallScriptPath = $MyInvocation.MyCommand.Path

function Get-WindowsPowerShellPath {
    if ($env:SystemRoot) {
        $candidate = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }

    return 'powershell.exe'
}

function Test-IsAdministrator {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Show-UninstallMessage {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Message,

        [Parameter(Mandatory = $true)]
        [ValidateSet('Information', 'Warning', 'Error')]
        [string]$Icon
    )

    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.MessageBox]::Show(
        $Message,
        'M2M XML EDITOR eltávolítása',
        'OK',
        $Icon
    ) | Out-Null
}

function Restart-ElevatedIfRequired {
    if ($InstallScope -ne 'AllUsers' -or (Test-IsAdministrator)) {
        return $false
    }

    $powerShellExe = Get-WindowsPowerShellPath
    $arguments = '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "{0}" -InstallPath "{1}" -AppVersion "{2}" -InstallScope "{3}"' -f `
        $script:UninstallScriptPath, `
        $InstallPath, `
        $AppVersion, `
        $InstallScope

    Start-Process -FilePath $powerShellExe -ArgumentList $arguments -Verb RunAs | Out-Null
    return $true
}

function Test-ProtectedInstallPath {
    $install = [System.IO.Path]::GetFullPath($InstallPath).TrimEnd('\')
    $protectedRoots = @()

    if ($env:LOCALAPPDATA) {
        $protectedRoots += [System.IO.Path]::GetFullPath(
            (Join-Path $env:LOCALAPPDATA 'M2M-XML-EDITOR')
        ).TrimEnd('\')
    }

    if ($env:ProgramData) {
        $protectedRoots += [System.IO.Path]::GetFullPath(
            (Join-Path $env:ProgramData 'M2M-XML-EDITOR')
        ).TrimEnd('\')
    }

    foreach ($root in $protectedRoots) {
        if ([System.StringComparer]::OrdinalIgnoreCase.Equals($install, $root) -or
            $install.StartsWith($root + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

function Start-TemporaryUninstaller {
    $source = $script:UninstallScriptPath

    if (-not $source -or -not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw 'Az eltávolító PowerShell script saját elérési útja nem határozható meg.'
    }

    $tempScript = Join-Path $env:TEMP ('M2M-XML-EDITOR-uninstall-' + [Guid]::NewGuid().ToString('N') + '.ps1')
    Copy-Item -LiteralPath $source -Destination $tempScript -Force

    $powerShellExe = Get-WindowsPowerShellPath
    $arguments = '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "{0}" -InstallPath "{1}" -AppVersion "{2}" -InstallScope "{3}" -FromTemp' -f `
        $tempScript, `
        $InstallPath, `
        $AppVersion, `
        $InstallScope

    Start-Process -FilePath $powerShellExe -ArgumentList $arguments | Out-Null
}

if (-not $FromTemp) {
    try {
        if (Restart-ElevatedIfRequired) {
            exit 0
        }

        Start-TemporaryUninstaller
        exit 0
    }
    catch {
        try {
            Show-UninstallMessage `
                -Message "Az eltávolítás nem indítható el.`r`n`r`n$($_.Exception.Message)" `
                -Icon 'Error'
        }
        catch {
        }
        exit 91
    }
}

$cacheBase = if ($InstallScope -eq 'AllUsers') { $env:ProgramData } else { $env:LOCALAPPDATA }
$JavawExe = Join-Path $cacheBase 'M2M-XML-EDITOR-Online-Setup\runtime\jdk-21\bin\javaw.exe'
if (-not (Test-Path -LiteralPath $JavawExe -PathType Leaf)) {
    $JavawExe = Join-Path $cacheBase 'M2M-XML-EDITOR-Online-Setup\runtime\jdk-17\bin\javaw.exe'
}
$UninstallerJar = Join-Path $InstallPath 'Uninstaller\uninstaller.jar'

try {
    if (Test-ProtectedInstallPath) {
        Show-UninstallMessage `
            -Message 'Biztonsági okból az eltávolítás megszakadt, mert a program telepítési útvonala egy védett M2M-XML-EDITOR adatkönyvtár alá mutat. Az adat- és konfigurációs könyvtárakat az uninstaller nem törölheti.' `
            -Icon 'Error'
        exit 24
    }

    $running = @(Get-Process -Name 'M2M XML EDITOR' -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        Show-UninstallMessage `
            -Message 'Az M2M XML EDITOR valamelyik verziója jelenleg fut. Zárja be az alkalmazást, majd indítsa újra az eltávolítást.' `
            -Icon 'Warning'
        exit 23
    }

    if (-not (Test-Path -LiteralPath $JavawExe -PathType Leaf)) {
        Show-UninstallMessage `
            -Message 'Az eltávolításhoz szükséges Java futtatókörnyezet (JDK 21 vagy kompatibilitási JDK 17) nem található.' `
            -Icon 'Error'
        exit 2
    }

    if (-not (Test-Path -LiteralPath $UninstallerJar -PathType Leaf)) {
        Show-UninstallMessage `
            -Message 'Az IzPack eltávolító komponens nem található.' `
            -Icon 'Error'
        exit 3
    }

    $quotedUninstallerJar = '"' + $UninstallerJar.Replace('"', '\"') + '"'
    $process = Start-Process -FilePath $JavawExe `
        -ArgumentList @('-jar', $quotedUninstallerJar) `
        -PassThru `
        -Wait

    if ($process.ExitCode -ne 0) {
        exit $process.ExitCode
    }

    $registryHive = if ($InstallScope -eq 'AllUsers') { 'HKLM:\SOFTWARE' } else { 'HKCU:\SOFTWARE' }
    $registryPath = "$registryHive\Microsoft\Windows\CurrentVersion\Uninstall\M2M XML EDITOR $AppVersion"
    if (Test-Path -LiteralPath $registryPath) {
        Remove-Item -LiteralPath $registryPath -Recurse -Force -ErrorAction SilentlyContinue
    }

    $desktopBase = if ($InstallScope -eq 'AllUsers') { [Environment]::GetFolderPath('CommonDesktopDirectory') } else { [Environment]::GetFolderPath('DesktopDirectory') }
    if ($desktopBase) {
        $desktopShortcut = Join-Path $desktopBase 'M2M XML EDITOR.lnk'
        if (Test-Path -LiteralPath $desktopShortcut) {
            try {
                $shell = New-Object -ComObject WScript.Shell
                $shortcut = $shell.CreateShortcut($desktopShortcut)
                $target = [System.IO.Path]::GetFullPath($shortcut.TargetPath)
                $expected = [System.IO.Path]::GetFullPath((Join-Path $InstallPath 'M2M XML EDITOR.exe'))
                if ($target -ieq $expected) {
                    Remove-Item -LiteralPath $desktopShortcut -Force -ErrorAction SilentlyContinue
                }
            }
            catch {
                # A parancsikon takarításának hibája nem akadályozhatja az eltávolítást.
            }
        }
    }

    $startMenuBase = if ($InstallScope -eq 'AllUsers') { $env:ProgramData } else { $env:APPDATA }
    if ($startMenuBase) {
        $startMenuVersionPath = Join-Path $startMenuBase "Microsoft\Windows\Start Menu\Programs\M2M XML EDITOR\$AppVersion"
        if (Test-Path -LiteralPath $startMenuVersionPath) {
            Remove-Item -LiteralPath $startMenuVersionPath -Recurse -Force -ErrorAction SilentlyContinue
        }

        $startMenuRoot = Join-Path $startMenuBase 'Microsoft\Windows\Start Menu\Programs\M2M XML EDITOR'
        if (Test-Path -LiteralPath $startMenuRoot) {
            $remaining = @(Get-ChildItem -LiteralPath $startMenuRoot -Force -ErrorAction SilentlyContinue)
            if ($remaining.Count -eq 0) {
                Remove-Item -LiteralPath $startMenuRoot -Force -ErrorAction SilentlyContinue
            }
        }
    }

    # A tartós adat- és konfigurációs könyvtárakhoz szándékosan nem nyúlunk.
    exit 0
}
catch {
    try {
        Show-UninstallMessage `
            -Message "Az eltávolítás nem fejeződött be megfelelően.`r`n`r`n$($_.Exception.Message)" `
            -Icon 'Warning'
    }
    catch {
    }
    exit 90
}
finally {
    $self = $MyInvocation.MyCommand.Path
    if ($FromTemp -and $self) {
        Remove-Item -LiteralPath $self -Force -ErrorAction SilentlyContinue
    }
}
