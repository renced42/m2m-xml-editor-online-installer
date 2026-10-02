using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using M2M.XmlEditor.Bootstrapper.Models;

namespace M2M.XmlEditor.Bootstrapper.Services;

public sealed class LocalSourcePackageService
{
    private readonly AppPaths _paths;
    private readonly BootstrapperConfig _config;
    private readonly LogService _log;

    public LocalSourcePackageService(AppPaths paths, BootstrapperConfig config, LogService log)
    {
        _paths = paths;
        _config = config;
        _log = log;
    }

    private static readonly Regex ReleaseValuePattern = new(
        @"^(?<version>\d+\.\d+\.\d+)-(?<date>\d{8})-(?<time>\d{6})(?:-RC\d+)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public LocalSourcePackage? FindAdjacentPackage()
    {
        if (!Directory.Exists(_paths.BaseDirectory)) return null;

        var repositoryName = _config.GithubRepository;
        var candidates = Directory.EnumerateFiles(_paths.BaseDirectory, "*.zip", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetFileName(path).StartsWith(repositoryName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (candidates.Length == 0) return null;
        if (candidates.Length > 1)
        {
            throw new InvalidOperationException(
                "Több helyi M2M XML EDITOR forrás-ZIP található a bootstrapper mellett. " +
                "Hagyjon ott pontosan egy, a repository nevével kezdődő ZIP csomagot. Találatok: " +
                string.Join(", ", candidates.Select(Path.GetFileName)));
        }

        var zipPath = candidates[0];
        var version = ResolveVersionFromReleaseFile(zipPath);
        ValidateSourceArchive(zipPath);
        _log.Write($"Helyi forráscsomag felismerve: {zipPath}; kötelező release a ZIP gyökerében vagy egy szinttel beljebb lévő RELEASE állományból: {version}");
        return new LocalSourcePackage(zipPath, version);
    }

    private static string ResolveVersionFromReleaseFile(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var rootReleaseEntries = archive.Entries
            .Where(entry => string.Equals(NormalizeArchivePath(entry.FullName), "RELEASE", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (rootReleaseEntries.Length > 1)
            throw new InvalidDataException($"A helyi forrás-ZIP gyökerében több RELEASE állomány található: {zipPath}");

        var releaseEntries = rootReleaseEntries.Length == 1
            ? rootReleaseEntries
            : archive.Entries
                .Where(entry => IsReleaseOneLevelBelowRoot(entry.FullName))
                .ToArray();

        if (releaseEntries.Length == 0)
            throw new InvalidDataException(
                $"A helyi forrás-ZIP gyökerében vagy pontosan egy szinttel beljebb nem található kötelező RELEASE állomány: {zipPath}");
        if (releaseEntries.Length > 1)
            throw new InvalidDataException(
                $"A helyi forrás-ZIP-ben egy szinttel a gyökér alatt több RELEASE állomány található, ezért a projekt gyökere nem egyértelmű: {zipPath}");

        string raw;
        using (var reader = new StreamReader(releaseEntries[0].Open()))
            raw = reader.ReadToEnd();

        var values = raw
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .ToArray();

        if (values.Length != 1)
            throw new InvalidDataException("A RELEASE állománynak pontosan egy nem üres release azonosítót kell tartalmaznia.");

        var release = values[0];
        var match = ReleaseValuePattern.Match(release);
        if (!match.Success)
            throw new InvalidDataException(
                $"A RELEASE állomány értéke érvénytelen: {release}. Elvárt forma: x.y.z-yyyymmdd-hhmmss[-RCx].");

        var dateTimeText = match.Groups["date"].Value + match.Groups["time"].Value;
        if (!DateTime.TryParseExact(
                dateTimeText,
                "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _))
            throw new InvalidDataException($"A RELEASE állomány release azonosítójában érvénytelen dátum/idő szerepel: {release}.");

        if (!SemVer.TryParse(release, out var parsedVersion) || parsedVersion is null)
            throw new InvalidDataException($"A RELEASE állomány release azonosítója nem értelmezhető verzióként: {release}.");

        return release;
    }


    private static string NormalizeArchivePath(string path)
    {
        return path.Replace('\\', '/').Trim('/');
    }

    private static bool IsReleaseOneLevelBelowRoot(string path)
    {
        var normalized = NormalizeArchivePath(path);
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && string.Equals(parts[1], "RELEASE", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateSourceArchive(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count == 0)
            throw new InvalidDataException($"A helyi forrás-ZIP üres: {zipPath}");

        var hasPom = archive.Entries.Any(entry =>
            entry.FullName.Replace('\\', '/').EndsWith("/pom.xml", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entry.FullName.Replace('\\', '/'), "pom.xml", StringComparison.OrdinalIgnoreCase));
        if (!hasPom)
            throw new InvalidDataException($"A helyi forrás-ZIP nem tartalmaz repository gyökér pom.xml állományt: {zipPath}");

        var hasBuildVersioned = archive.Entries.Any(entry =>
            entry.FullName.Replace('\\', '/').EndsWith("/build-versioned.cmd", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entry.FullName.Replace('\\', '/'), "build-versioned.cmd", StringComparison.OrdinalIgnoreCase));
        if (!hasBuildVersioned)
            throw new InvalidDataException($"A helyi forrás-ZIP nem tartalmaz repository gyökér build-versioned.cmd állományt: {zipPath}");
    }
}
