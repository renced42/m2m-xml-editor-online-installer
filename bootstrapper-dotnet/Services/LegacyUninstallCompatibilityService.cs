using System.ComponentModel;
using System.Diagnostics;
using M2M.XmlEditor.Bootstrapper.Models;

namespace M2M.XmlEditor.Bootstrapper.Services;

public sealed class LegacyUninstallCompatibilityService
{
    private readonly WindowsPlatformService _platform;
    private readonly RuntimeService _runtime;
    private readonly LogService _log;

    public LegacyUninstallCompatibilityService(WindowsPlatformService platform, RuntimeService runtime, LogService log)
    {
        _platform = platform;
        _runtime = runtime;
        _log = log;
    }

    public async Task UninstallAsync(LegacyUninstallInfo installation, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        _platform.AssertApplicationNotRunning();
        progress?.Report(new ProgressInfo("Kompatibilitási eltávolítás előkészítése", installation.Version, 0));
        var jdk = await _runtime.EnsureJdkAsync(progress, ct);
        var javaw = Path.Combine(jdk, "bin", "javaw.exe");
        if (!File.Exists(javaw))
            throw new FileNotFoundException("A kompatibilitási eltávolításhoz szükséges Java futtatókörnyezet nem található.", javaw);
        if (!File.Exists(installation.UninstallerJar))
            throw new FileNotFoundException("A korábbi IzPack uninstaller.jar nem található.", installation.UninstallerJar);

        _log.Write($"Korábbi IzPack registry-listeneres telepítés eltávolítása UAC-emeléssel: {installation.Version}");
        var psi = new ProcessStartInfo
        {
            FileName = javaw,
            Arguments = $"-jar \"{installation.UninstallerJar}\"",
            WorkingDirectory = Path.GetDirectoryName(installation.UninstallerJar)!,
            UseShellExecute = true,
            Verb = "runas"
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("A rendszergazdai kompatibilitási eltávolító nem indítható el.");
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"A korábbi IzPack eltávolító hibával állt le ({process.ExitCode}).");

        if (installation.Scope == InstallationScope.CurrentUser)
            _platform.RemoveCurrentUserUninstallEntry(installation.Version);

        if (Directory.Exists(installation.InstallDirectory))
            _log.Write($"Figyelem: a korábbi programkönyvtár az IzPack eltávolítás után még létezik: {installation.InstallDirectory}");
        else
            _log.Write($"Korábbi telepítés eltávolítva: {installation.Version}");
    }
}
