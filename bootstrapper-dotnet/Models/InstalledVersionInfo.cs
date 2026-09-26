namespace M2M.XmlEditor.Bootstrapper.Models;

public sealed record InstalledVersionInfo(
    string Version,
    string ProgramDirectory,
    string DataDirectory,
    InstallationScope Scope,
    bool Legacy = false);
