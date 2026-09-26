using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;
using M2M.XmlEditor.Bootstrapper.Models;

namespace M2M.XmlEditor.Bootstrapper.Services;

[SupportedOSPlatform("windows")]
public sealed class WindowsPlatformService
{
    private readonly AppPaths _paths;
    private readonly LogService _log;
    private readonly string _applicationName;

    public WindowsPlatformService(AppPaths paths, LogService log, string applicationName)
    {
        _paths = paths;
        _log = log;
        _applicationName = applicationName;
    }

    public IReadOnlyList<InstalledVersionInfo> GetInstalledVersions()
    {
        var result = new List<InstalledVersionInfo>();
        ReadInstalledVersions(Registry.CurrentUser, InstallationScope.CurrentUser, result);
        ReadInstalledVersions(Registry.LocalMachine, InstallationScope.AllUsers, result);

        return result
            .OrderByDescending(x =>
            {
                SemVer.TryParse(x.Version, out var version);
                return version;
            }, Comparer<SemVer?>.Create((a, b) => a?.CompareTo(b) ?? -1))
            .ToArray();
    }

    public InstalledVersionInfo? GetNewestInstalledVersion() => GetInstalledVersions().FirstOrDefault();



    public IReadOnlyList<LegacyUninstallInfo> GetLegacyIzPackRegistryListenerInstallations()
    {
        var result = new List<LegacyUninstallInfo>();
        foreach (var installed in GetInstalledVersions())
        {
            var jar = Path.Combine(installed.ProgramDirectory, "Uninstaller", "uninstaller.jar");
            if (!File.Exists(jar) || !ContainsRegistryUninstallerListener(jar)) continue;
            result.Add(new LegacyUninstallInfo(installed.Version, installed.ProgramDirectory, installed.Scope, jar));
        }
        return result;
    }

    public void RemoveCurrentUserUninstallEntry(string version)
    {
        var path = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\M2M XML EDITOR {version}";
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Write($"A HKCU eltávolítási bejegyzés takarítása nem sikerült: {ex.Message}");
        }
    }

    private static bool ContainsRegistryUninstallerListener(string uninstallerJar)
    {
        try
        {
            using var archive = ZipFile.OpenRead(uninstallerJar);
            return archive.Entries.Any(entry =>
                entry.FullName.EndsWith("RegistryUninstallerListener.class", StringComparison.OrdinalIgnoreCase));
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public void RegisterUninstallEntry(string version, string installDirectory, string uninstallLauncher)
    {
        var hive = _paths.Scope == InstallationScope.AllUsers ? Registry.LocalMachine : Registry.CurrentUser;
        var keyPath = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\M2M XML EDITOR {version}";
        using var key = hive.CreateSubKey(keyPath, writable: true)
            ?? throw new InvalidOperationException($"Nem hozható létre a Windows eltávolítási bejegyzés: {keyPath}");

        var scope = _paths.Scope == InstallationScope.AllUsers ? "AllUsers" : "CurrentUser";
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\powershell.exe");
        if (!File.Exists(powershell)) powershell = "powershell.exe";

        var uninstallCommand = $"\"{powershell}\" -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass " +
                               $"-File \"{uninstallLauncher}\" -InstallPath \"{installDirectory}\" " +
                               $"-AppVersion \"{version}\" -InstallScope \"{scope}\"";

        key.SetValue("DisplayName", _applicationName, RegistryValueKind.String);
        key.SetValue("DisplayVersion", version, RegistryValueKind.String);
        key.SetValue("Publisher", "Nemzeti Adó- és Vámhivatal", RegistryValueKind.String);
        key.SetValue("InstallLocation", installDirectory, RegistryValueKind.String);
        key.SetValue("DisplayIcon", Path.Combine(installDirectory, "app.ico"), RegistryValueKind.String);
        key.SetValue("UninstallString", uninstallCommand, RegistryValueKind.String);
        key.SetValue("QuietUninstallString", uninstallCommand, RegistryValueKind.String);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);

        _log.Write($"Windows eltávolítási bejegyzés létrehozva: {(_paths.Scope == InstallationScope.AllUsers ? "HKLM" : "HKCU")}\\{keyPath}");
    }


    public void CreateApplicationShortcuts(string version, string installDirectory, string executablePath)
    {
        if (!File.Exists(executablePath))
            throw new FileNotFoundException("A parancsikon célalkalmazása nem található.", executablePath);

        var desktop = Environment.GetFolderPath(
            _paths.Scope == InstallationScope.AllUsers
                ? Environment.SpecialFolder.CommonDesktopDirectory
                : Environment.SpecialFolder.DesktopDirectory);
        var programs = Environment.GetFolderPath(
            _paths.Scope == InstallationScope.AllUsers
                ? Environment.SpecialFolder.CommonPrograms
                : Environment.SpecialFolder.Programs);

        if (string.IsNullOrWhiteSpace(desktop) || string.IsNullOrWhiteSpace(programs))
            throw new InvalidOperationException("A Windows Asztal vagy Start menü könyvtára nem oldható fel.");

        var desktopShortcut = Path.Combine(desktop, $"{_applicationName}.lnk");
        var startMenuDirectory = Path.Combine(programs, _applicationName, version);
        var startMenuShortcut = Path.Combine(startMenuDirectory, $"{_applicationName}.lnk");

        CreateWindowsShortcutOutOfProcess(desktopShortcut, executablePath, installDirectory);
        CreateWindowsShortcutOutOfProcess(startMenuShortcut, executablePath, installDirectory);

        _log.Write($"Asztali parancsikon létrehozva: {desktopShortcut}");
        _log.Write($"Start menü parancsikon létrehozva: {startMenuShortcut}");
    }

    private static void CreateWindowsShortcutOutOfProcess(string shortcutPath, string targetPath, string workingDirectory)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);

        static string PsLiteral(string value) => "'" + value.Replace("'", "''") + "'";

        var script = string.Join(";", new[]
        {
            "$ErrorActionPreference='Stop'",
            "$shell=New-Object -ComObject WScript.Shell",
            $"$shortcut=$shell.CreateShortcut({PsLiteral(shortcutPath)})",
            $"$shortcut.TargetPath={PsLiteral(targetPath)}",
            $"$shortcut.WorkingDirectory={PsLiteral(workingDirectory)}",
            $"$shortcut.Description={PsLiteral(Path.GetFileNameWithoutExtension(targetPath))}",
            $"$shortcut.IconLocation={PsLiteral(targetPath + ",0")}",
            "$shortcut.Save()"
        });

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\powershell.exe");
        if (!File.Exists(powershell)) powershell = "powershell.exe";

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = powershell,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}"
        }) ?? throw new InvalidOperationException("A Windows parancsikon-létrehozó folyamat nem indítható.");

        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError;
            throw new InvalidOperationException(
                $"A Windows parancsikon létrehozása nem sikerült (kilépési kód: {process.ExitCode}). {detail.Trim()}");
        }
    }

    public void AssertApplicationNotRunning()
    {
        var running = new List<Process>();
        var programRoots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "M2M XML EDITOR"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "M2M XML EDITOR")
        };

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var byName = string.Equals(process.ProcessName, _applicationName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(process.ProcessName, _applicationName.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase);
                var path = process.MainModule?.FileName;
                var byPath = !string.IsNullOrWhiteSpace(path) && programRoots.Any(root =>
                    Path.GetFullPath(path).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

                if (byName || byPath) running.Add(process);
                else process.Dispose();
            }
            catch
            {
                process.Dispose();
            }
        }

        if (running.Count == 0) return;

        var ids = string.Join(", ", running.Select(p => p.Id));
        foreach (var process in running) process.Dispose();
        _log.Write($"Futó M2M XML EDITOR példány észlelve. PID: {ids}");
        throw new InvalidOperationException("Az M2M XML EDITOR valamelyik verziója jelenleg fut. Zárja be az alkalmazást, majd próbálja újra.");
    }

    private static void ReadInstalledVersions(RegistryKey hive, InstallationScope scope, ICollection<InstalledVersionInfo> result)
    {
        using var root = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        if (root is null) return;

        foreach (var subName in root.GetSubKeyNames())
        {
            if (!subName.StartsWith("M2M XML EDITOR ", StringComparison.OrdinalIgnoreCase)) continue;

            using var sub = root.OpenSubKey(subName);
            var displayVersion = sub?.GetValue("DisplayVersion") as string;
            var installLocation = sub?.GetValue("InstallLocation") as string;
            if (!SemVer.TryParse(displayVersion, out var sem) || sem is null || string.IsNullOrWhiteSpace(installLocation)) continue;

            result.Add(new InstalledVersionInfo(
                sem.Normalized,
                installLocation,
                GetDataDirectory(scope, sem.Normalized),
                scope));
        }

        using var legacy = root.OpenSubKey("M2M XML EDITOR");
        var legacyVersion = legacy?.GetValue("DisplayVersion") as string;
        var legacyLocation = legacy?.GetValue("InstallLocation") as string;
        if (SemVer.TryParse(legacyVersion, out var legacySem) && legacySem is not null && !string.IsNullOrWhiteSpace(legacyLocation))
        {
            result.Add(new InstalledVersionInfo(
                legacySem.Normalized,
                legacyLocation,
                scope == InstallationScope.AllUsers
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "M2M-XML-EDITOR")
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "M2M-XML-EDITOR"),
                scope,
                true));
        }
    }

    private static string GetDataDirectory(InstallationScope scope, string version)
    {
        var root = scope == InstallationScope.AllUsers
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "M2M-XML-EDITOR")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "M2M-XML-EDITOR");
        return Path.Combine(root, version);
    }
}
