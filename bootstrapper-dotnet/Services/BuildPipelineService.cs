using System.Text.RegularExpressions;
using M2M.XmlEditor.Bootstrapper.Models;

namespace M2M.XmlEditor.Bootstrapper.Services;

public sealed class BuildPipelineService
{
    private readonly BootstrapperConfig _config; private readonly AppPaths _paths; private readonly DownloadService _download; private readonly ZipService _zip; private readonly RuntimeService _runtime; private readonly ProcessService _process; private readonly WindowsPlatformService _platform; private readonly DatabaseProfileService _database; private readonly LogService _log; private readonly ProxySettings _proxySettings;
    private readonly Func<TlsServerCertificateInfo, Task<bool>> _certificateTrustPrompt;
    private readonly TlsTrustService _tlsTrust;

    public BuildPipelineService(BootstrapperConfig config, AppPaths paths, DownloadService download, ZipService zip, RuntimeService runtime, ProcessService process, WindowsPlatformService platform, DatabaseProfileService database, LogService log, ProxySettings proxySettings, Func<TlsServerCertificateInfo, Task<bool>> certificateTrustPrompt)
    {
        _config=config; _paths=paths; _download=download; _zip=zip; _runtime=runtime; _process=process; _platform=platform; _database=database; _log=log; _proxySettings=proxySettings.Clone();
        _certificateTrustPrompt = certificateTrustPrompt ?? throw new ArgumentNullException(nameof(certificateTrustPrompt));
        _tlsTrust = new TlsTrustService(paths, process, log);
    }

    public async Task<InstallationResult> RunAsync(ReleaseInfo release, bool cloneH2, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        if (!SemVer.TryParse(release.TagName, out var selected) || selected is null)
            throw new InvalidOperationException("Érvénytelen release-verzió.");

        _platform.AssertApplicationNotRunning();

        progress?.Report(new ProgressInfo($"Java {_config.JavaMajorVersion} előkészítése", string.Empty, 5));
        var jdk = await _runtime.EnsureJdkAsync(progress, ct);
        progress?.Report(new ProgressInfo("Maven előkészítése", string.Empty, 12));
        var maven = await _runtime.EnsureMavenAsync(jdk, progress, ct);
        progress?.Report(new ProgressInfo("Git előkészítése", string.Empty, 18));
        var git = await _runtime.EnsureGitAsync(progress, ct);

        progress?.Report(new ProgressInfo("MinGit GitHub kapcsolat ellenőrzése", release.TagName, 22));
        await TestGitConnectivityAsync(git, progress, ct);

        progress?.Report(new ProgressInfo("Git forrás ellenőrzése", release.TagName, 25));
        var source = await PrepareSourceAsync(release.TagName, git, progress, ct);

        progress?.Report(new ProgressInfo("Verziózott M2M XML EDITOR build", release.TagName, 45));
        var generatedBuildVersion = await BuildApplicationVersionedAsync(source, jdk, maven, git, progress, ct);

        // A termék hivatalos verziója mindig a felhasználó által kiválasztott GitHub release/tag.
        // A build-versioned.cmd által előállított VERSION_RELEASE kizárólag diagnosztikai adat.
        // A build-version.env hiánya ezért nem lehet telepítést megszakító hiba.
        var releaseVersion = release.TagName.Trim();
        if (string.IsNullOrWhiteSpace(generatedBuildVersion))
            _log.Write($"Kiválasztott GitHub release: {releaseVersion}. A build-version.env nem készült el; a GitHub release verziója marad a hivatalos termékverzió.");
        else
            _log.Write($"Kiválasztott GitHub release: {releaseVersion}; build-versioned.cmd VERSION_RELEASE: {generatedBuildVersion} (csak diagnosztika).");

        var newest = _platform.GetNewestInstalledVersion();
        if (newest is not null && SemVer.TryParse(newest.Version, out var installed) && installed is not null && selected.CompareTo(installed) < 0)
            throw new InvalidOperationException($"A kiválasztott {releaseVersion} release régebbi a telepített {installed.Normalized} verziónál. Downgrade nem engedélyezett.");

        var targetData = _paths.DataDirectory(releaseVersion);
        if (cloneH2)
        {
            if (newest is null || !SemVer.TryParse(newest.Version, out var cloneSourceVersion) || cloneSourceVersion is null || selected.CompareTo(cloneSourceVersion) <= 0)
                throw new InvalidOperationException("H2 adatmásolás csak újabb GitHub release telepítésekor használható.");
            _database.CloneH2Data(newest, releaseVersion);
        }
        else
        {
            Directory.CreateDirectory(targetData);
        }

        if (_paths.Scope == InstallationScope.AllUsers)
            await EnsureAllUsersDataPermissionsAsync(targetData, ct);

        var jar = FindApplicationJar(source);
        progress?.Report(new ProgressInfo("Windows alkalmazáskép készítése", Path.GetFileName(jar), 68));
        var runId = CreateRunId(releaseVersion);
        var image = await BuildAppImageAsync(source, jar, jdk, releaseVersion, targetData, runId, ct);
        var isolatedInstallerProject = PrepareIsolatedInstallerProject(runId);
        ConfigureInstallerScope(isolatedInstallerProject, releaseVersion);
        StageInstallerPayload(image, isolatedInstallerProject);
        PrepareIzPackStaging(isolatedInstallerProject);
        _platform.AssertApplicationNotRunning();
        progress?.Report(new ProgressInfo("IzPack telepítő fordítása", releaseVersion, 82));
        var installer = await BuildIzPackInstallerAsync(jdk, maven, releaseVersion, isolatedInstallerProject, ct);
        _platform.AssertApplicationNotRunning();
        progress?.Report(new ProgressInfo("Alkalmazás telepítése", releaseVersion, 90));
        await RunInstallerAsync(jdk, installer, releaseVersion, runId, ct);

        var installDirectory = _paths.ProgramDirectory(releaseVersion);
        var installedExecutable = Path.Combine(installDirectory, $"{_config.ApplicationName}.exe");
        var uninstaller = Path.Combine(installDirectory, "Uninstaller", "uninstaller.jar");
        var uninstallLauncher = Path.Combine(installDirectory, "Uninstaller", "M2M XML EDITOR eltavolitasa.ps1");

        var missing = new[]
        {
            (Path: installedExecutable, Description: "alkalmazás EXE"),
            (Path: uninstaller, Description: "IzPack uninstaller"),
            (Path: uninstallLauncher, Description: "eltávolító PowerShell indító")
        }.Where(item => !File.Exists(item.Path)).ToArray();

        if (missing.Length > 0)
        {
            var discoveredExecutables = Directory.Exists(installDirectory)
                ? Directory.GetFiles(installDirectory, $"{_config.ApplicationName}.exe", SearchOption.AllDirectories)
                : Array.Empty<string>();
            if (discoveredExecutables.Length > 0)
                _log.Write("A várt helytől eltérő alkalmazás-EXE található: " + string.Join(" | ", discoveredExecutables));

            var missingText = string.Join(", ", missing.Select(item => $"{item.Description}: {item.Path}"));
            throw new InvalidOperationException(
                "Az IzPack folyamat hibakód nélkül befejeződött, de a telepítés nem teljes. Hiányzó elemek: " + missingText);
        }

        _platform.RegisterUninstallEntry(releaseVersion, installDirectory, uninstallLauncher);
        _platform.CreateApplicationShortcuts(releaseVersion, installDirectory, installedExecutable);
        _log.Write($"Telepítés utóellenőrzése sikeres: {installDirectory}");
        progress?.Report(new ProgressInfo("A telepítés sikeresen befejeződött", releaseVersion, 100));
        return new InstallationResult(releaseVersion, installedExecutable);
    }

    private async Task TestGitConnectivityAsync(string gitHome, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var gitExe = Path.Combine(gitHome, "cmd", "git.exe");
        var repositoryUrl = $"https://github.com/{_config.GithubOwner}/{_config.GithubRepository}.git";
        var environment = ProxyNetworkService.CreateGitProcessEnvironment(_proxySettings);
        var output = new List<string>();

        _log.Write("MinGit GitHub kapcsolat ellenőrzése: git ls-remote HEAD");
        try
        {
            await _process.RunAsync(
                gitExe,
                new[] { "ls-remote", repositoryUrl, "HEAD" },
                _paths.CacheRoot,
                environment,
                line =>
                {
                    output.Add(line);
                    progress?.Report(new ProgressInfo("MinGit GitHub kapcsolat", line, 22));
                },
                ct);
            _log.Write("MinGit GitHub kapcsolat: OK");
        }
        catch (InvalidOperationException ex)
        {
            var diagnostic = string.Join(" | ", output.TakeLast(8));
            if (diagnostic.Contains("response 407", StringComparison.OrdinalIgnoreCase) ||
                diagnostic.Contains("Proxy Authentication Required", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "A MinGit nem tudott hitelesíteni a proxy felé (HTTP 407). " +
                    "Windows rendszerproxy módban a bootstrapper a Git for Windows SSPI/Negotiate-NTLM " +
                    "hitelesítését próbálja használni. Ellenőrizd, hogy a vállalati proxy engedélyezi-e ezt a kliens számára. " +
                    "Git diagnosztika: " + diagnostic, ex);
            }

            throw new InvalidOperationException(
                "A MinGit GitHub kapcsolatellenőrzése sikertelen. Git diagnosztika: " + diagnostic, ex);
        }
    }

    private async Task<string> PrepareSourceAsync(string tag, string gitHome, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        // A forrás-repository tartós cache-ben marad. Így ugyanazt a release-t nem
        // klónozzuk le minden telepítésnél új, véletlenszerű könyvtárba.
        // A rövid útvonal továbbra is fontos a Windows MAX_PATH problémák miatt.
        var cloneRoot = Path.Combine(_paths.CacheRoot, "g");
        Directory.CreateDirectory(cloneRoot);
        var source = Path.Combine(cloneRoot, "repo");
        var gitExe = Path.Combine(gitHome, "cmd", "git.exe");
        var repositoryUrl = $"https://github.com/{_config.GithubOwner}/{_config.GithubRepository}.git";
        var proxyEnvironment = ProxyNetworkService.CreateGitProcessEnvironment(_proxySettings);

        if (!Directory.Exists(Path.Combine(source, ".git")))
        {
            if (Directory.Exists(source))
                Directory.Delete(source, recursive: true);

            var legacyClone = await FindLegacyCloneAsync(
                gitExe, cloneRoot, repositoryUrl, source, proxyEnvironment, ct);

            if (legacyClone is not null)
            {
                Directory.Move(legacyClone, source);
                _log.Write($"Korábbi véletlenszerű Git clone áthelyezve a tartós cache-be: {legacyClone} -> {source}");
            }
            else
            {
                progress?.Report(new ProgressInfo("Git repository első letöltése", tag, 27));
                await _process.RunAsync(
                    gitExe,
                    new[] { "-c", "core.longpaths=true", "clone", "--no-checkout", repositoryUrl, source },
                    cloneRoot,
                    proxyEnvironment,
                    line => progress?.Report(new ProgressInfo("Git clone", line, 30)),
                    ct);
            }

            await _process.RunAsync(
                gitExe,
                new[] { "-C", source, "config", "core.longpaths", "true" },
                cloneRoot,
                proxyEnvironment,
                null,
                ct);
        }
        else
        {
            var origin = await GetRepositoryOriginUrlAsync(gitExe, source, proxyEnvironment, ct);
            if (!RepositoryUrlsEqual(origin, repositoryUrl))
            {
                _log.Write($"A Git forráscache másik repositoryhoz tartozik ({origin ?? "ismeretlen"}); új cache készül.");
                Directory.Delete(source, recursive: true);
                await _process.RunAsync(
                    gitExe,
                    new[] { "-c", "core.longpaths=true", "clone", "--no-checkout", repositoryUrl, source },
                    cloneRoot,
                    proxyEnvironment,
                    line => progress?.Report(new ProgressInfo("Git clone", line, 30)),
                    ct);
                await _process.RunAsync(
                    gitExe,
                    new[] { "-C", source, "config", "core.longpaths", "true" },
                    cloneRoot,
                    proxyEnvironment,
                    null,
                    ct);
            }
            else
            {
                _log.Write($"Git forráscache megtalálva: {source}");
            }
        }

        var localTag = await GetLocalTagObjectIdAsync(gitExe, source, tag, proxyEnvironment, ct);
        var remoteTag = await GetRemoteTagObjectIdAsync(gitExe, cloneRoot, repositoryUrl, tag, proxyEnvironment, ct);

        _log.Write($"Git release ellenőrzés. Tag: {tag}; lokális: {localTag ?? "nincs"}; távoli: {remoteTag}");

        if (!string.Equals(localTag, remoteTag, StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report(new ProgressInfo("Git release frissítése", tag, 30));
            _log.Write(localTag is null
                ? $"A {tag} release még nincs a lokális Git cache-ben. Csak a kiválasztott tag kerül letöltésre."
                : $"A {tag} release lokális és távoli Git objektuma eltér. A kiválasztott tag frissítése szükséges.");

            await _process.RunAsync(
                gitExe,
                new[]
                {
                    "-C", source, "fetch", "--force", "--no-tags", "origin",
                    $"refs/tags/{tag}:refs/tags/{tag}"
                },
                cloneRoot,
                proxyEnvironment,
                line => progress?.Report(new ProgressInfo("Git fetch", line, 32)),
                ct);

            localTag = await GetLocalTagObjectIdAsync(gitExe, source, tag, proxyEnvironment, ct);
            if (!string.Equals(localTag, remoteTag, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"A {tag} release frissítése után a lokális Git objektum ({localTag ?? "nincs"}) " +
                    $"nem egyezik a távoli objektummal ({remoteTag}).");
            }
        }
        else
        {
            progress?.Report(new ProgressInfo("Git release cache használata", tag, 32));
            _log.Write($"A {tag} release változatlan, újraletöltés nem szükséges.");
        }

        // A korábbi buildből maradt target és egyéb generált állományok ne befolyásolják
        // az új telepítést. A repository cache megmarad, csak a working tree tisztul.
        await _process.RunAsync(
            gitExe,
            new[] { "-C", source, "reset", "--hard" },
            cloneRoot,
            proxyEnvironment,
            null,
            ct);

        await _process.RunAsync(
            gitExe,
            new[] { "-C", source, "clean", "-fdx" },
            cloneRoot,
            proxyEnvironment,
            null,
            ct);

        await _process.RunAsync(
            gitExe,
            new[] { "-C", source, "checkout", "--detach", tag },
            cloneRoot,
            proxyEnvironment,
            line => progress?.Report(new ProgressInfo("Git checkout", line, 34)),
            ct);

        _log.Write($"Git forrás előkészítve. Tag: {tag}; repository: {source}");
        return source;
    }

    private async Task<string?> FindLegacyCloneAsync(
        string gitExe,
        string cloneRoot,
        string repositoryUrl,
        string persistentSource,
        IDictionary<string, string> environment,
        CancellationToken ct)
    {
        foreach (var candidate in Directory.EnumerateDirectories(cloneRoot)
                     .Where(path => !string.Equals(path, persistentSource, StringComparison.OrdinalIgnoreCase))
                     .Where(path => Directory.Exists(Path.Combine(path, ".git")))
                     .OrderByDescending(path => Directory.GetLastWriteTimeUtc(path)))
        {
            var origin = await GetRepositoryOriginUrlAsync(gitExe, candidate, environment, ct);
            if (RepositoryUrlsEqual(origin, repositoryUrl))
                return candidate;
        }

        return null;
    }

    private async Task<string?> GetRepositoryOriginUrlAsync(
        string gitExe,
        string source,
        IDictionary<string, string> environment,
        CancellationToken ct)
    {
        var output = new List<string>();
        try
        {
            await _process.RunAsync(
                gitExe,
                new[] { "-C", source, "remote", "get-url", "origin" },
                source,
                environment,
                line => output.Add(line.Trim()),
                ct);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        return output.FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
    }

    private static bool RepositoryUrlsEqual(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
            return false;

        static string Normalize(string value) => value.Trim().TrimEnd('/').ToLowerInvariant();
        return string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);
    }

    private async Task<string?> GetLocalTagObjectIdAsync(
        string gitExe,
        string source,
        string tag,
        IDictionary<string, string> environment,
        CancellationToken ct)
    {
        var output = new List<string>();
        await _process.RunAsync(
            gitExe,
            new[] { "-C", source, "for-each-ref", "--format=%(objectname)", $"refs/tags/{tag}" },
            source,
            environment,
            line => output.Add(line.Trim()),
            ct);

        return output.FirstOrDefault(line => Regex.IsMatch(line, "^[0-9a-fA-F]{40,64}$"));
    }

    private async Task<string> GetRemoteTagObjectIdAsync(
        string gitExe,
        string workingDirectory,
        string repositoryUrl,
        string tag,
        IDictionary<string, string> environment,
        CancellationToken ct)
    {
        var output = new List<string>();
        await _process.RunAsync(
            gitExe,
            new[] { "ls-remote", "--tags", repositoryUrl, $"refs/tags/{tag}" },
            workingDirectory,
            environment,
            line => output.Add(line.Trim()),
            ct);

        foreach (var line in output)
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 &&
                string.Equals(parts[1], $"refs/tags/{tag}", StringComparison.Ordinal) &&
                Regex.IsMatch(parts[0], "^[0-9a-fA-F]{40,64}$"))
            {
                return parts[0];
            }
        }

        throw new InvalidOperationException($"A kiválasztott GitHub release tag nem található a távoli repositoryban: {tag}");
    }

    private async Task<string?> BuildApplicationVersionedAsync(
        string source,
        string jdk,
        string maven,
        string git,
        IProgress<ProgressInfo>? progress,
        CancellationToken ct)
    {
        var buildScript = Path.Combine(source, "build-versioned.cmd");
        if (!File.Exists(buildScript))
            throw new FileNotFoundException("A repository gyökerében nem található a build-versioned.cmd.", buildScript);

        var cmdExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        if (!File.Exists(cmdExe)) cmdExe = "cmd.exe";
        var gitCmd = Path.Combine(git, "cmd");
        var path = string.Join(";", new[]
        {
            Path.Combine(jdk, "bin"),
            Path.Combine(maven, "bin"),
            gitCmd,
            Environment.GetEnvironmentVariable("PATH") ?? string.Empty
        });
        var environment = new Dictionary<string, string>
        {
            ["JAVA_HOME"] = jdk,
            ["MAVEN_HOME"] = maven,
            ["PATH"] = path
        };
        ProxyNetworkService.MergeGitInto(environment, _proxySettings);

        var userMavenSettings = ProxyNetworkService.GetUserMavenSettingsPath();
        if (File.Exists(userMavenSettings))
            _log.Write($"Maven settings: {userMavenSettings} (meglévő felhasználói/céges beállítás, változtatás nélkül)");
        else
            _log.Write("Maven settings: nincs felhasználói ~/.m2/settings.xml; a bootstrapper Maven alapbeállítása/proxykörnyezete használatos.");

        MavenDiagnosticsService.WriteDiagnostics(_log, jdk, _proxySettings, environment);

        var extraArguments = _config.VersionedBuildArguments.Trim();
        var mavenSettings = ProxyNetworkService.CreateMavenSettingsFile(_proxySettings, Path.Combine(_paths.Workspace, "proxy"));
        if (!string.IsNullOrWhiteSpace(mavenSettings))
        {
            extraArguments = string.IsNullOrWhiteSpace(extraArguments)
                ? $"-s \"{mavenSettings}\""
                : $"{extraArguments} -s \"{mavenSettings}\"";
        }

        // A munkakonyvtar maga a repository gyokere, ezert a batch fajlt relativ nevvel
        // hivjuk. A Maven proxy settings utvonala csak argumentumkent kap idezojelet.
        var command = string.IsNullOrWhiteSpace(extraArguments)
            ? "call build-versioned.cmd"
            : $"call build-versioned.cmd {extraArguments}";

        _log.Write("A projekt verziózott buildje a repository build-versioned.cmd wrapperével indul.");
        try
        {
            await RunWithTlsTrustRecoveryAsync(
                "M2M XML EDITOR Maven build",
                jdk,
                environment,
                lineObserver => _process.RunAsync(
                    cmdExe,
                    new[] { "/d", "/c", command },
                    source,
                    environment,
                    line =>
                    {
                        lineObserver(line);
                        progress?.Report(new ProgressInfo("build-versioned.cmd", line, 55));
                    },
                    ct),
                ct);
        }
        finally
        {
            ProxyNetworkService.DeleteMavenSettingsFile(mavenSettings);
        }

        var versionFile = Path.Combine(source, "target", "generated-version", "build-version.env");
        if (!File.Exists(versionFile))
        {
            _log.Write($"A build-version.env nem jött létre ({versionFile}). Ez nem hiba: a hivatalos termékverzió a kiválasztott GitHub release/tag.");
            return null;
        }

        var values = File.ReadAllLines(versionFile)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);

        if (values.TryGetValue("VERSION_TIMESTAMP", out var timestamp) && !string.IsNullOrWhiteSpace(timestamp))
            _log.Write($"Generált build timestamp: {timestamp}");

        if (!values.TryGetValue("VERSION_RELEASE", out var version) || string.IsNullOrWhiteSpace(version))
        {
            _log.Write("A build-version.env nem tartalmaz VERSION_RELEASE értéket. Ez csak diagnosztikai adat; a GitHub release verziója marad érvényben.");
            return null;
        }

        return version;
    }

    private string FindApplicationJar(string source)
    {
        var target = Path.Combine(source,_config.WebModuleTarget.Replace('/',Path.DirectorySeparatorChar));
        var jars = Directory.Exists(target) ? Directory.GetFiles(target,"*.jar",SearchOption.TopDirectoryOnly).Where(f=>!f.EndsWith("-sources.jar",StringComparison.OrdinalIgnoreCase) && !f.EndsWith("-javadoc.jar",StringComparison.OrdinalIgnoreCase)).ToArray() : Array.Empty<string>();
        if (jars.Length == 0) throw new FileNotFoundException($"Nem található futtatható JAR: {target}");
        return jars.OrderByDescending(File.GetLastWriteTimeUtc).First();
    }

    private async Task<string> BuildAppImageAsync(string source,string jar,string jdk,string version,string dataDir,string runId,CancellationToken ct)
    {
        var runRoot = Path.Combine(_paths.Workspace, "runs", runId);
        Directory.CreateDirectory(runRoot);
        var outDir = Path.Combine(runRoot,"app-image"); Directory.CreateDirectory(outDir);
        var input = Path.Combine(runRoot,"jpackage-input"); Directory.CreateDirectory(input); File.Copy(jar,Path.Combine(input,Path.GetFileName(jar)),true);
        var icon = Path.Combine(_paths.InstallerProjectDirectory,"src","main","izpack","resources","app.ico");
        var args = new List<string>{"--type","app-image","--name",_config.ApplicationName,"--input",input,"--main-jar",Path.GetFileName(jar),"--main-class",_config.MainClass,"--dest",outDir,"--java-options",$"-Dapp.data.dir={dataDir}"};
        if (File.Exists(icon)) { args.Add("--icon"); args.Add(icon); }
        await _process.RunAsync(Path.Combine(jdk,"bin","jpackage.exe"),args,source,null,null,ct);
        var image = Path.Combine(outDir,_config.ApplicationName); if (!Directory.Exists(image)) throw new InvalidOperationException("A jpackage app-image nem jött létre."); return image;
    }

    private string PrepareIsolatedInstallerProject(string runId)
    {
        var target = Path.Combine(_paths.Workspace, "runs", runId, "installer-project");
        CopyDirectory(_paths.InstallerProjectDirectory, target, static name =>
            !name.Equals("target", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("payload", StringComparison.OrdinalIgnoreCase));
        _log.Write($"Izolált IzPack projekt előkészítve: {target}");
        return target;
    }


    private void ConfigureInstallerScope(string installerProject, string version)
    {
        var installXml = Path.Combine(installerProject, "src", "main", "izpack", "install.xml");
        var installXmlText = File.ReadAllText(installXml);
        var defaultInstallPath = _paths.ProgramDirectory(version)
            .Replace("&", "&amp;")
            .Replace("\"", "&quot;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
        installXmlText = System.Text.RegularExpressions.Regex.Replace(
            installXmlText,
            @"<variable name=""INSTALL_PATH"" value=""[^""]*""\s*/>",
            $"<variable name=\"INSTALL_PATH\" value=\"{defaultInstallPath}\"/>");
        File.WriteAllText(installXml, installXmlText);

        var scope = _paths.Scope == InstallationScope.AllUsers ? "AllUsers" : "CurrentUser";
        _log.Write($"IzPack telepítési hatókör beállítva: {scope}, cél: {_paths.ProgramDirectory(version)}. " +
                   "A Windows uninstall-regisztrációt a bootstrapper végzi a sikeres telepítés után.");
    }

    private async Task EnsureAllUsersDataPermissionsAsync(string dataDirectory, CancellationToken ct)
    {
        Directory.CreateDirectory(dataDirectory);
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var icacls = Path.Combine(systemDirectory, "icacls.exe");
        await _process.RunAsync(
            icacls,
            new[] { dataDirectory, "/grant", "*S-1-5-32-545:(OI)(CI)M", "/T", "/C" },
            dataDirectory,
            null,
            null,
            ct);
        _log.Write($"Minden felhasználó számára írható adatkönyvtár előkészítve: {dataDirectory}");
    }

    private void StageInstallerPayload(string image, string installerProject)
    {
        var payload = Path.Combine(installerProject,"src","main","izpack","payload");
        Directory.CreateDirectory(payload);
        CopyDirectory(image,payload);
        var icon = Path.Combine(installerProject,"src","main","izpack","resources","app.ico"); if (File.Exists(icon)) File.Copy(icon,Path.Combine(payload,"app.ico"),true);
        _log.Write($"IzPack payload előkészítve: {payload}");
    }

    private void PrepareIzPackStaging(string installerProject)
    {
        var izpackSource = Path.Combine(installerProject, "src", "main", "izpack");
        var staging = Path.Combine(installerProject, "target", "staging");
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        CopyDirectory(izpackSource, staging);

        var required = new[]
        {
            Path.Combine(staging, "resources", "app.ico"),
            Path.Combine(staging, "resources", "LICENSE.txt"),
            Path.Combine(staging, "payload", $"{_config.ApplicationName}.exe")
        };

        var missing = required.Where(path => !File.Exists(path)).ToArray();
        if (missing.Length > 0)
            throw new FileNotFoundException("Az IzPack staging előkészítése hiányos: " + string.Join(", ", missing));

        _log.Write($"IzPack staging előkészítve: {staging}");
    }

    private async Task<string> BuildIzPackInstallerAsync(string jdk,string maven,string version,string installerProject,CancellationToken ct)
    {
        var environment = new Dictionary<string, string>
        {
            ["JAVA_HOME"] = jdk,
            ["PATH"] = $"{Path.Combine(jdk,"bin")};{Path.Combine(maven,"bin")};{Environment.GetEnvironmentVariable("PATH")}"
        };
        ProxyNetworkService.MergeInto(environment, _proxySettings);
        var mavenSettings = ProxyNetworkService.CreateMavenSettingsFile(_proxySettings, Path.Combine(_paths.Workspace, "proxy"));
        var arguments = new List<string> { "package", $"-Dinstaller.app.version={version}" };
        if (!string.IsNullOrWhiteSpace(mavenSettings))
        {
            arguments.Add("-s");
            arguments.Add(mavenSettings);
        }

        try
        {
            await RunWithTlsTrustRecoveryAsync(
                "IzPack Maven build",
                jdk,
                environment,
                lineObserver => _process.RunAsync(
                    Path.Combine(maven,"bin","mvn.cmd"),
                    arguments,
                    installerProject,
                    environment,
                    lineObserver,
                    ct),
                ct);
        }
        finally
        {
            ProxyNetworkService.DeleteMavenSettingsFile(mavenSettings);
        }

        var target = Path.Combine(installerProject,"target");
        var jar = Directory.GetFiles(target,"M2M-XML-EDITOR-Installer-*.jar").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        return jar ?? throw new FileNotFoundException("Az IzPack installer JAR nem található.");
    }


    private async Task RunWithTlsTrustRecoveryAsync(
        string operationName,
        string jdkHome,
        IDictionary<string, string> environment,
        Func<Action<string>, Task> run,
        CancellationToken ct)
    {
        var recentLines = new Queue<string>();

        void Observe(string line)
        {
            recentLines.Enqueue(line);
            while (recentLines.Count > 200) recentLines.Dequeue();
        }

        try
        {
            await run(Observe);
            return;
        }
        catch (InvalidOperationException firstFailure)
        {
            if (!TryFindPkixEndpoint(recentLines, out var endpoint))
                throw;

            _log.Write($"{operationName}: PKIX tanúsítványhiba észlelve. TLS végpont: {endpoint.Host}:{endpoint.Port}");

            TlsServerCertificateInfo certificate;
            try
            {
                certificate = await _tlsTrust.FetchServerCertificateAsync(endpoint.Host, endpoint.Port, ct);
            }
            catch (Exception certificateError)
            {
                throw new InvalidOperationException(
                    $"A Maven PKIX hibát jelzett a(z) {endpoint.Host}:{endpoint.Port} végponton, de a szerver tanúsítványa nem kérdezhető le automatikusan. " +
                    $"TLS lekérési hiba: {certificateError.Message}",
                    firstFailure);
            }

            LogCertificate(certificate);
            var trusted = _tlsTrust.IsPreviouslyTrusted(certificate);
            if (trusted)
            {
                _log.Write($"A tanúsítvány korábban már elfogadásra került ugyanilyen SHA-256 fingerprinttel: {certificate.Sha256Fingerprint}");
            }
            else
            {
                trusted = await _certificateTrustPrompt(certificate);
                if (trusted) _tlsTrust.RememberTrust(certificate);
            }

            if (!trusted)
            {
                throw new InvalidOperationException(
                    $"A(z) {certificate.Endpoint} szerver tanúsítványát a felhasználó nem jelölte megbízhatónak. A Maven build nem folytatható.",
                    firstFailure);
            }

            var trustStore = await _tlsTrust.EnsureTrustStoreAsync(certificate, jdkHome, ct);
            TlsTrustService.ApplyTrustStore(environment, trustStore);
            _log.Write($"{operationName}: Maven újrapróbálása a bootstrapper saját truststore-jával.");
            MavenDiagnosticsService.WriteDiagnostics(_log, jdkHome, _proxySettings, environment);

            recentLines.Clear();
            await run(Observe);
        }
    }

    private void LogCertificate(TlsServerCertificateInfo certificate)
    {
        _log.Write($"TLS szerver: {certificate.Endpoint}");
        _log.Write($"TLS Subject: {certificate.Subject}");
        _log.Write($"TLS Issuer: {certificate.Issuer}");
        _log.Write($"TLS érvényesség: {certificate.NotBefore:yyyy-MM-dd HH:mm:ss} - {certificate.NotAfter:yyyy-MM-dd HH:mm:ss}");
        _log.Write($"TLS SHA-256 fingerprint: {certificate.Sha256Fingerprint}");
    }

    private static bool TryFindPkixEndpoint(IEnumerable<string> lines, out Uri endpoint)
    {
        endpoint = null!;
        var buffered = lines.ToArray();
        var hasPkixFailure = buffered.Any(line =>
            line.Contains("PKIX path building failed", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("certificate_unknown", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("SunCertPathBuilderException", StringComparison.OrdinalIgnoreCase));
        if (!hasPkixFailure) return false;

        foreach (var line in buffered.Reverse())
        {
            var matches = Regex.Matches(line, @"https://[^\s)\]]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            foreach (Match match in matches.Cast<Match>().Reverse())
            {
                var value = match.Value.TrimEnd('.', ',', ';', ':');
                if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
                {
                    endpoint = uri;
                    return true;
                }
            }
        }

        return false;
    }

    private async Task RunInstallerAsync(string jdk, string installer, string version, string runId, CancellationToken ct)
    {
        var runRoot = Path.Combine(_paths.Workspace, "runs", runId);
        Directory.CreateDirectory(runRoot);
        var defaultsFile = Path.Combine(runRoot, "izpack.defaults");
        var installPath = _paths.ProgramDirectory(version).Replace('\\', '/');
        await File.WriteAllLinesAsync(defaultsFile, new[]
        {
            $"INSTALL_PATH={installPath}"
        }, ct);

        var args = new[]
        {
            "-Dfile.encoding=UTF-8",
            $"-DINSTALL_PATH={_paths.ProgramDirectory(version)}",
            "-jar", installer,
            "-language", "hun",
            "-defaults-file", defaultsFile,
            "-auto"
        };

        _log.Write($"IzPack automatikus telepítés indítása. Célkönyvtár: {_paths.ProgramDirectory(version)}");
        await _process.RunAsync(
            Path.Combine(jdk, "bin", "java.exe"),
            args,
            Path.GetDirectoryName(installer)!,
            null,
            null,
            ct,
            successfulCompletionLine: "[ Automated installation done ]",
            completionGracePeriod: TimeSpan.FromSeconds(5));
        _log.Write("Az IzPack automatikus telepítőfolyamata befejeződött; a telepített állományok utóellenőrzése következik.");
    }

    private static string CreateRunId(string version)
    {
        var safeVersion = string.Concat(version.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_'));
        return $"{safeVersion}-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
    }

    private static void CopyDirectory(string source,string target,Func<string,bool>? includeDirectory = null)
    {
        Directory.CreateDirectory(target);
        foreach(var f in Directory.GetFiles(source)) File.Copy(f,Path.Combine(target,Path.GetFileName(f)),true);
        foreach(var d in Directory.GetDirectories(source))
        {
            var name = Path.GetFileName(d);
            if (includeDirectory is not null && !includeDirectory(name)) continue;
            CopyDirectory(d,Path.Combine(target,name),includeDirectory);
        }
    }
}
