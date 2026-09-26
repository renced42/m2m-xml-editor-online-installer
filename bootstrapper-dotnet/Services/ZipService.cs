using System.IO.Compression;

namespace M2M.XmlEditor.Bootstrapper.Services;

public sealed class ZipService
{
    public bool IsValidZip(string path)
    {
        if (!File.Exists(path)) return false;
        try { using var zip = ZipFile.OpenRead(path); return zip.Entries.Count > 0; } catch { return false; }
    }

    public async Task ExtractCleanAsync(string zipFile, string destination, string displayName, IProgress<Models.ProgressInfo>? progress, CancellationToken ct)
    {
        if (Directory.Exists(destination)) Directory.Delete(destination, true);
        Directory.CreateDirectory(destination);
        var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(zipFile);
        var total = archive.Entries.Sum(e => e.Length); long processed = 0;
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Érvénytelen ZIP bejegyzés: {entry.FullName}");
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true);
            var buffer = new byte[64 * 1024]; int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct); processed += read;
                var percent = total > 0 ? (int)Math.Min(100, processed * 100 / total) : 0;
                progress?.Report(new Models.ProgressInfo($"Kicsomagolás: {displayName}", entry.FullName, percent));
            }
        }
    }
}
