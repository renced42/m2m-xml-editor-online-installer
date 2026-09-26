using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace M2M.XmlEditor.Bootstrapper.Services;

public static class EmbeddedAssetsService
{
    private const string InstallerProjectResource = "M2M.XmlEditor.Bootstrapper.Embedded.installer-project.zip";


    private const string BundledRuntimePrefix = "M2M.XmlEditor.Bootstrapper.Embedded.BundledRuntime.";
    private const string BundledRuntimeManifestResource = BundledRuntimePrefix + "manifest.json";

    public static bool TryMaterializeBundledRuntimePackage(string packageKey, string targetFile)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = packageKey switch
        {
            "jdk" => BundledRuntimePrefix + "temurin-jdk.zip",
            "maven" => BundledRuntimePrefix + "apache-maven.zip",
            "git" => BundledRuntimePrefix + "mingit.zip",
            _ => throw new ArgumentOutOfRangeException(nameof(packageKey), packageKey, "Ismeretlen bundled runtime csomag.")
        };

        using var packageStream = assembly.GetManifestResourceStream(resourceName);
        if (packageStream is null)
            return false;

        var expectedSha256 = ReadBundledPackageSha256(assembly, packageKey);
        Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
        var temporaryFile = targetFile + ".bundled.tmp";
        try
        {
            using (var output = File.Create(temporaryFile))
                packageStream.CopyTo(output);

            var actualSha256 = ComputeSha256(temporaryFile);
            if (!actualSha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"A beágyazott {packageKey} runtime csomag SHA-256 ellenőrzése sikertelen.");

            File.Move(temporaryFile, targetFile, overwrite: true);
            return true;
        }
        finally
        {
            if (File.Exists(temporaryFile))
                File.Delete(temporaryFile);
        }
    }

    public static void ExtractBundledLegalDocuments(string targetDirectory)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var licenseArchiveStream = assembly.GetManifestResourceStream(BundledRuntimePrefix + "third-party-licenses.zip");
        using var noticeStream = assembly.GetManifestResourceStream(BundledRuntimePrefix + "THIRD-PARTY-NOTICES.txt");
        if (licenseArchiveStream is null && noticeStream is null)
            return;

        if (Directory.Exists(targetDirectory))
            Directory.Delete(targetDirectory, recursive: true);
        Directory.CreateDirectory(targetDirectory);

        if (licenseArchiveStream is not null)
        {
            using var archive = new ZipArchive(licenseArchiveStream, ZipArchiveMode.Read, leaveOpen: false);
            foreach (var entry in archive.Entries)
            {
                var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                var destination = Path.GetFullPath(Path.Combine(targetDirectory, "licenses", relative));
                var root = Path.GetFullPath(Path.Combine(targetDirectory, "licenses")) + Path.DirectorySeparatorChar;
                if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Érvénytelen bundled licenc ZIP bejegyzés: {entry.FullName}");

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
            }
        }

        if (noticeStream is not null)
        {
            using var output = File.Create(Path.Combine(targetDirectory, "THIRD-PARTY-NOTICES.txt"));
            noticeStream.CopyTo(output);
        }
    }

    private static string ReadBundledPackageSha256(Assembly assembly, string packageKey)
    {
        using var manifestStream = assembly.GetManifestResourceStream(BundledRuntimeManifestResource)
            ?? throw new InvalidOperationException("A bundled runtime manifest hiányzik.");
        using var document = JsonDocument.Parse(manifestStream);
        if (!document.RootElement.TryGetProperty("packages", out var packages) ||
            !packages.TryGetProperty(packageKey, out var package) ||
            !package.TryGetProperty("sha256", out var sha256) ||
            string.IsNullOrWhiteSpace(sha256.GetString()))
            throw new InvalidDataException($"A bundled runtime manifest nem tartalmaz SHA-256 értéket ehhez: {packageKey}.");
        return sha256.GetString()!;
    }

    private static string ComputeSha256(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static void ExtractInstallerProject(string targetDirectory)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(InstallerProjectResource)
            ?? throw new InvalidOperationException($"Hiányzik a beágyazott installer projekt: {InstallerProjectResource}");

        var parent = Path.GetDirectoryName(targetDirectory)
            ?? throw new InvalidOperationException("Az installer projekt célkönyvtára érvénytelen.");
        Directory.CreateDirectory(parent);

        var tempDirectory = targetDirectory + ".tmp-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            foreach (var entry in archive.Entries)
            {
                var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                var destination = Path.GetFullPath(Path.Combine(tempDirectory, relative));
                var root = Path.GetFullPath(tempDirectory) + Path.DirectorySeparatorChar;
                if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Érvénytelen beágyazott ZIP bejegyzés: {entry.FullName}");

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
            }

            ValidateInstallerProject(tempDirectory);

            if (Directory.Exists(targetDirectory))
                Directory.Delete(targetDirectory, recursive: true);
            Directory.Move(tempDirectory, targetDirectory);
        }
        catch
        {
            if (Directory.Exists(tempDirectory))
                Directory.Delete(tempDirectory, recursive: true);
            throw;
        }
    }

    private static void ValidateInstallerProject(string directory)
    {
        var required = new[]
        {
            Path.Combine(directory, "pom.xml"),
            Path.Combine(directory, ".mvn", "jvm.config"),
            Path.Combine(directory, "src", "main", "izpack", "install.xml"),
            Path.Combine(directory, "src", "main", "izpack", "resources", "app.ico")
        };

        var missing = required.Where(path => !File.Exists(path)).ToArray();
        if (missing.Length > 0)
            throw new FileNotFoundException("A beágyazott installer projekt hiányos: " + string.Join(", ", missing));
    }
}
