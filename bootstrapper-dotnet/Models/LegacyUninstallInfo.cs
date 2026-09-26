namespace M2M.XmlEditor.Bootstrapper.Models;

public sealed record LegacyUninstallInfo(string Version, string InstallDirectory, InstallationScope Scope, string UninstallerJar);
