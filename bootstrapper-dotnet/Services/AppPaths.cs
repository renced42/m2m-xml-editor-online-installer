using M2M.XmlEditor.Bootstrapper.Models;

namespace M2M.XmlEditor.Bootstrapper.Services;

public sealed class AppPaths
{
    public string BaseDirectory { get; }
    public string InstallerProjectDirectory { get; }
    public string CacheRoot { get; }
    public string Downloads { get; }
    public string Runtime { get; }
    public string Workspace { get; }
    public string Logs { get; }
    public string ReleaseCacheFile { get; }
    public string ProgramRoot { get; }
    public string DataRoot { get; }
    public InstallationScope Scope { get; }

    public AppPaths(BootstrapperConfig config, InstallationScope scope)
    {
        Scope = scope;
        BaseDirectory = AppContext.BaseDirectory;

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        if (scope == InstallationScope.AllUsers)
        {
            CacheRoot = Path.Combine(programData, config.CacheDirectoryName);
            ProgramRoot = Path.Combine(programFiles, "M2M XML EDITOR");
            DataRoot = Path.Combine(programData, "M2M-XML-EDITOR");
        }
        else
        {
            CacheRoot = Path.Combine(local, config.CacheDirectoryName);
            ProgramRoot = Path.Combine(local, "Programs", "M2M XML EDITOR");
            DataRoot = Path.Combine(local, "M2M-XML-EDITOR");
        }

        Downloads = Path.Combine(CacheRoot, "downloads");
        Runtime = Path.Combine(CacheRoot, "runtime");
        Workspace = Path.Combine(CacheRoot, "workspace");
        Logs = Path.Combine(CacheRoot, "logs");
        ReleaseCacheFile = Path.Combine(CacheRoot, "cache", "releases-r29.35.json");
        InstallerProjectDirectory = Path.Combine(Workspace, "embedded-installer-project", "r29.35");

        foreach (var dir in new[]
                 {
                     CacheRoot, Downloads, Runtime, Workspace, Logs,
                     Path.GetDirectoryName(ReleaseCacheFile)!, ProgramRoot, DataRoot
                 })
        {
            Directory.CreateDirectory(dir);
        }
    }

    public string ProgramDirectory() => ProgramRoot;
    public string DataDirectory() => DataRoot;
}
