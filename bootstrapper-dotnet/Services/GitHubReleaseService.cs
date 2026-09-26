using System.Net.Http.Headers;
using System.Text.Json;
using M2M.XmlEditor.Bootstrapper.Models;

namespace M2M.XmlEditor.Bootstrapper.Services;

public sealed class GitHubReleaseService
{
    private readonly HttpClient _http;
    private readonly BootstrapperConfig _config;
    private readonly AppPaths _paths;
    private readonly LogService _log;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public GitHubReleaseService(BootstrapperConfig config, AppPaths paths, LogService log, ProxySettings proxySettings)
    {
        _config = config; _paths = paths; _log = log;
        _http = ProxyNetworkService.CreateHttpClient(proxySettings, TimeSpan.FromMinutes(2));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("M2M-XML-EDITOR-Online-Setup/0.9");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<IReadOnlyList<ReleaseInfo>> GetCatalogAsync(CancellationToken cancellationToken)
    {
        try
        {
            var baseUrl = $"https://api.github.com/repos/{_config.GithubOwner}/{_config.GithubRepository}/releases";
            _log.Write("GitHub latest stable és prerelease lista lekérése...");
            var latestJson = await _http.GetStringAsync(baseUrl + "/latest", cancellationToken);
            var releasesJson = await _http.GetStringAsync(baseUrl + $"?per_page={_config.GithubReleasePageSize}", cancellationToken);
            var latest = ParseOne(latestJson);
            var prereleases = ParseMany(releasesJson).Where(r => r.Prerelease && !r.Draft);
            var catalog = new[] { latest }.Concat(prereleases)
                .Where(r => SemVer.TryParse(r.TagName, out _))
                .GroupBy(r => r.TagName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(_paths.ReleaseCacheFile)!);
            await File.WriteAllTextAsync(_paths.ReleaseCacheFile, JsonSerializer.Serialize(catalog, _jsonOptions), cancellationToken);
            return Sort(catalog);
        }
        catch (Exception ex) when (File.Exists(_paths.ReleaseCacheFile))
        {
            _log.Write($"FIGYELMEZTETÉS: GitHub API nem érhető el, cache használata: {ex.Message}");
            var cached = JsonSerializer.Deserialize<List<ReleaseInfo>>(await File.ReadAllTextAsync(_paths.ReleaseCacheFile, cancellationToken), _jsonOptions) ?? new();
            return Sort(cached);
        }
    }

    private static ReleaseInfo ParseOne(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ParseElement(doc.RootElement);
    }

    private static IEnumerable<ReleaseInfo> ParseMany(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(ParseElement).ToArray();
    }

    private static ReleaseInfo ParseElement(JsonElement item)
    {
        var draft = item.GetProperty("draft").GetBoolean();
        var prerelease = item.GetProperty("prerelease").GetBoolean();
        var tag = item.GetProperty("tag_name").GetString() ?? string.Empty;
        var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? tag : tag;
        DateTimeOffset? published = null;
        if (item.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(p.GetString(), out var parsed)) published = parsed;
        return new ReleaseInfo(tag, name, prerelease, draft, published);
    }

    private static IReadOnlyList<ReleaseInfo> Sort(IEnumerable<ReleaseInfo> releases) => releases
        .OrderByDescending(r => { SemVer.TryParse(r.TagName, out var v); return v; }, Comparer<SemVer?>.Create((a,b) => a?.CompareTo(b) ?? -1))
        .ToArray();
}
