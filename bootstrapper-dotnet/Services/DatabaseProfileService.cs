using M2M.XmlEditor.Bootstrapper.Models;

namespace M2M.XmlEditor.Bootstrapper.Services;

public sealed class DatabaseProfileService
{
    private readonly AppPaths _paths;
    private readonly LogService _log;

    public DatabaseProfileService(AppPaths paths, LogService log) { _paths = paths; _log = log; }

    public string DetectDatabaseKind(string dataDirectory)
    {
        if (!Directory.Exists(dataDirectory)) return "Ismeretlen";
        var candidates = new List<string>();
        candidates.AddRange(Directory.EnumerateFiles(dataDirectory, "*.properties", SearchOption.TopDirectoryOnly));
        candidates.AddRange(Directory.EnumerateFiles(dataDirectory, "*.yml", SearchOption.TopDirectoryOnly));
        candidates.AddRange(Directory.EnumerateFiles(dataDirectory, "*.yaml", SearchOption.TopDirectoryOnly));
        var config = Path.Combine(dataDirectory, "config");
        if (Directory.Exists(config))
        {
            candidates.AddRange(Directory.EnumerateFiles(config, "*.properties", SearchOption.TopDirectoryOnly));
            candidates.AddRange(Directory.EnumerateFiles(config, "*.yml", SearchOption.TopDirectoryOnly));
            candidates.AddRange(Directory.EnumerateFiles(config, "*.yaml", SearchOption.TopDirectoryOnly));
        }
        foreach (var file in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string content;
            try { content = File.ReadAllText(file); } catch { continue; }
            if (content.Contains("jdbc:h2:", StringComparison.OrdinalIgnoreCase)) return "H2";
            if (content.Contains("jdbc:mysql:", StringComparison.OrdinalIgnoreCase)) return "MySQL";
            if (content.Contains("jdbc:postgresql:", StringComparison.OrdinalIgnoreCase)) return "PostgreSQL";
            if (content.Contains("jdbc:oracle:", StringComparison.OrdinalIgnoreCase)) return "Oracle";
        }
        return "Ismeretlen";
    }

    public void CloneH2Data(InstalledVersionInfo source, string targetVersion)
    {
        var kind = DetectDatabaseKind(source.DataDirectory);
        if (!string.Equals(kind, "H2", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Az előző verzió adatbázisa nem H2 ({kind}), ezért a konfiguráció/adatok automatikus másolása tiltott.");
        var target = _paths.DataDirectory(targetVersion);
        if (Directory.Exists(target)) Directory.Delete(target, true);
        CopyDirectory(source.DataDirectory, target);
        _log.Write($"H2 konfiguráció és adatok másolva: {source.DataDirectory} -> {target}");
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
        foreach (var directory in Directory.GetDirectories(source)) CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
    }
}
