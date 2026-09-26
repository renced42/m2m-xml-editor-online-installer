using System.Security.Cryptography;
using M2M.XmlEditor.Bootstrapper.Models;

namespace M2M.XmlEditor.Bootstrapper.Services;

public sealed class RuntimeService
{
    private readonly BootstrapperConfig _config;
    private readonly AppPaths _paths;
    private readonly DownloadService _download;
    private readonly ZipService _zip;
    private readonly LogService _log;

    public RuntimeService(
        BootstrapperConfig config,
        AppPaths paths,
        DownloadService download,
        ZipService zip,
        LogService log)
    {
        _config = config;
        _paths = paths;
        _download = download;
        _zip = zip;
        _log = log;
    }

    public async Task<string> EnsureJdkAsync(IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var home = Path.Combine(_paths.Runtime, $"jdk-{_config.JavaMajorVersion}");
        if (File.Exists(Path.Combine(home, "bin", "java.exe")) && File.Exists(Path.Combine(home, "bin", "jpackage.exe")))
        {
            _log.Write($"Java {_config.JavaMajorVersion} cache használata: {home}");
            return home;
        }

        var zipFile = Path.Combine(_paths.Downloads, $"temurin-jdk-{_config.JavaMajorVersion}-windows-x64.zip");
        if (!_zip.IsValidZip(zipFile))
        {
            if (EmbeddedAssetsService.TryMaterializeBundledRuntimePackage("jdk", zipFile))
            {
                _log.Write($"Beágyazott Temurin JDK {_config.JavaMajorVersion} csomag használata: {zipFile}");
            }
            else
            {
                var downloadUrl = await _download.ResolveTemurinPackageUrlAsync(_config.JavaMajorVersion, ct);
                await _download.DownloadAsync(
                    downloadUrl,
                    zipFile,
                    $"Temurin JDK {_config.JavaMajorVersion}",
                    progress,
                    ct);
            }
        }
        else
        {
            _log.Write($"Java {_config.JavaMajorVersion} ZIP cache használata: {zipFile}");
        }

        var temp = Path.Combine(_paths.Runtime, $"jdk-{_config.JavaMajorVersion}-extract");
        await _zip.ExtractCleanAsync(zipFile, temp, $"Temurin JDK {_config.JavaMajorVersion}", progress, ct);
        var candidate = Directory.GetDirectories(temp).SingleOrDefault() ?? temp;
        if (Directory.Exists(home)) Directory.Delete(home, true);
        Directory.Move(candidate, home);
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
        if (!File.Exists(Path.Combine(home, "bin", "jpackage.exe")))
            throw new InvalidOperationException($"A Java {_config.JavaMajorVersion} JDK előkészítése nem sikerült.");
        return home;
    }

    public async Task<string> EnsureMavenAsync(string javaHome, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var home = Path.Combine(_paths.Runtime, $"apache-maven-{_config.MavenVersion}");
        if (File.Exists(Path.Combine(home, "bin", "mvn.cmd")))
        {
            _log.Write($"Maven cache használata: {home}");
            return home;
        }

        var zipFile = Path.Combine(_paths.Downloads, $"apache-maven-{_config.MavenVersion}-bin.zip");
        if (!_zip.IsValidZip(zipFile))
        {
            if (EmbeddedAssetsService.TryMaterializeBundledRuntimePackage("maven", zipFile))
            {
                _log.Write($"Beágyazott Apache Maven {_config.MavenVersion} csomag használata: {zipFile}");
            }
            else
            {
                await _download.DownloadAsync(
                    $"https://archive.apache.org/dist/maven/maven-3/{_config.MavenVersion}/binaries/apache-maven-{_config.MavenVersion}-bin.zip",
                    zipFile,
                    $"Apache Maven {_config.MavenVersion}",
                    progress,
                    ct);
            }
        }
        else
        {
            _log.Write($"Maven ZIP cache használata: {zipFile}");
        }

        var temp = Path.Combine(_paths.Runtime, "maven-extract");
        await _zip.ExtractCleanAsync(zipFile, temp, $"Apache Maven {_config.MavenVersion}", progress, ct);
        var candidate = Directory.GetDirectories(temp).SingleOrDefault() ?? temp;
        if (Directory.Exists(home)) Directory.Delete(home, true);
        Directory.Move(candidate, home);
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
        if (!File.Exists(Path.Combine(javaHome, "bin", "java.exe")))
            throw new InvalidOperationException("A Maven előkészítése közben eltűnt a Java runtime.");
        return home;
    }

    public async Task<string> EnsureGitAsync(IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var home = Path.Combine(_paths.Runtime, $"mingit-{_config.GitMinVersion}");
        var gitExe = Path.Combine(home, "cmd", "git.exe");
        if (File.Exists(gitExe))
        {
            _log.Write($"MinGit cache használata: {home}");
            return home;
        }

        var package = Path.Combine(_paths.Downloads, $"MinGit-{_config.GitMinVersion}-64-bit.zip");
        if (!_zip.IsValidZip(package) || !Sha256Matches(package, _config.GitMinSha256))
        {
            if (File.Exists(package)) File.Delete(package);
            if (EmbeddedAssetsService.TryMaterializeBundledRuntimePackage("git", package))
            {
                _log.Write($"Beágyazott MinGit {_config.GitMinVersion} csomag használata: {package}");
            }
            else
            {
                await _download.DownloadAsync(
                    _config.GitMinDownloadUrl,
                    package,
                    $"MinGit {_config.GitMinVersion}",
                    progress,
                    ct);
            }
        }
        else
        {
            _log.Write($"MinGit ZIP cache használata: {package}");
        }

        if (!_zip.IsValidZip(package))
            throw new InvalidOperationException("A letöltött MinGit csomag nem érvényes ZIP.");
        if (!Sha256Matches(package, _config.GitMinSha256))
            throw new InvalidOperationException("A letöltött MinGit csomag SHA-256 ellenőrzése sikertelen.");

        var temp = Path.Combine(_paths.Runtime, "mingit-extract");
        await _zip.ExtractCleanAsync(package, temp, $"MinGit {_config.GitMinVersion}", progress, ct);

        if (!File.Exists(Path.Combine(temp, "cmd", "git.exe")))
            throw new InvalidOperationException("A MinGit kicsomagolása nem sikerült.");

        if (Directory.Exists(home)) Directory.Delete(home, true);
        Directory.Move(temp, home);
        _log.Write($"MinGit előkészítve: {home}");
        return home;
    }

    private static bool Sha256Matches(string file, string expected)
    {
        if (string.IsNullOrWhiteSpace(expected)) return true;
        using var stream = File.OpenRead(file);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        return actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
}
