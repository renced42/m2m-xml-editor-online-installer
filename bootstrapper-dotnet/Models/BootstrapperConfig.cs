namespace M2M.XmlEditor.Bootstrapper.Models;

public sealed class BootstrapperConfig
{
    public string ApplicationName { get; init; } = "M2M XML EDITOR";
    public string GithubOwner { get; init; } = "nav-gov-hu";
    public string GithubRepository { get; init; } = "m2m-xml-editor";
    public int JavaMajorVersion { get; init; } = 21;
    public string MavenVersion { get; init; } = "3.9.16";
    public string GitMinVersion { get; init; } = "2.55.0.5";
    public string GitMinDownloadUrl { get; init; } = "https://github.com/git-for-windows/git/releases/download/v2.55.0.windows.5/MinGit-2.55.0.5-64-bit.zip";
    public string GitMinSha256 { get; init; } = "56d7b226b7693196cfc71fef26568f536c4a021ab6c37ff2db4287bed908e96e";
    public string VersionedBuildArguments { get; init; } = "-DskipTests";
    public string WebModuleTarget { get; init; } = "nav-xsd-parser-tool-web/target";
    public string MainClass { get; init; } = "org.springframework.boot.loader.launch.JarLauncher";
    public string CacheDirectoryName { get; init; } = "M2M-XML-EDITOR-Online-Setup";
    public string InstallerOutputPattern { get; init; } = "M2M-XML-EDITOR-Installer-*.jar";
    public int GithubReleasePageSize { get; init; } = 100;
}
