using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using M2M.XmlEditor.Bootstrapper.Models;

namespace M2M.XmlEditor.Bootstrapper.Services;

public static class ProxyNetworkService
{
    private static readonly Uri HttpProbeUri = new("http://example.com/");
    private static readonly Uri HttpsProbeUri = new("https://github.com/");

    public static HttpClient CreateHttpClient(ProxySettings settings, TimeSpan timeout)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = true };
        var bypassPatterns = ParseNonProxyHosts(settings.NonProxyHosts);

        switch (settings.Mode)
        {
            case ProxyMode.None:
                handler.UseProxy = false;
                break;
            case ProxyMode.Custom:
                handler.UseProxy = true;
                handler.Proxy = CreateCustomProxy(settings, bypassPatterns);
                break;
            default:
                handler.UseProxy = true;
                if (bypassPatterns.Count > 0)
                    handler.Proxy = new BypassAwareWebProxy(HttpClient.DefaultProxy, bypassPatterns, CredentialCache.DefaultCredentials);
                handler.DefaultProxyCredentials = CredentialCache.DefaultCredentials;
                break;
        }

        return new HttpClient(handler)
        {
            Timeout = timeout,
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
    }

    public static Dictionary<string, string> CreateProcessEnvironment(ProxySettings settings)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        switch (settings.Mode)
        {
            case ProxyMode.None:
                SetProxyEnvironment(environment, string.Empty, string.Empty);
                environment["ALL_PROXY"] = string.Empty;
                environment["all_proxy"] = string.Empty;
                environment["NO_PROXY"] = "*";
                environment["no_proxy"] = "*";
                break;
            case ProxyMode.Custom:
                Validate(settings);
                var customHttp = BuildAuthenticatedProxyUri(settings.HttpProxyUrl, settings.Username, settings.Password);
                var customHttps = BuildAuthenticatedProxyUri(settings.EffectiveHttpsProxyUrl, settings.Username, settings.Password);
                SetProxyEnvironment(environment, customHttp, customHttps);
                SetNoProxyEnvironment(environment, settings.NonProxyHosts);
                AddMavenProxyOptions(environment, settings.HttpProxyUrl, settings.EffectiveHttpsProxyUrl, settings.NonProxyHosts);
                break;
            default:
                var systemHttp = GetSystemProxyUri(HttpProbeUri);
                var systemHttps = GetSystemProxyUri(HttpsProbeUri);
                SetProxyEnvironment(environment, systemHttp ?? string.Empty, systemHttps ?? systemHttp ?? string.Empty);
                SetNoProxyEnvironment(environment, settings.NonProxyHosts);
                AddMavenProxyOptions(environment, systemHttp, systemHttps ?? systemHttp, settings.NonProxyHosts);
                break;
        }
        return environment;
    }

    public static Dictionary<string, string> CreateGitProcessEnvironment(ProxySettings settings)
    {
        var environment = CreateProcessEnvironment(settings);

        // A publikus repositoryhoz nincs GitHub credential helperre szükség. Így a
        // MinGit nem jelenít meg CredentialHelperSelector ablakot és nem kérhet
        // interaktív hitelesítést.
        environment["GIT_TERMINAL_PROMPT"] = "0";
        environment["GCM_INTERACTIVE"] = "Never";
        environment["GIT_CONFIG_COUNT"] = "2";
        environment["GIT_CONFIG_KEY_0"] = "credential.helper";
        environment["GIT_CONFIG_VALUE_0"] = string.Empty;
        environment["GIT_CONFIG_KEY_1"] = "http.version";
        environment["GIT_CONFIG_VALUE_1"] = "HTTP/1.1";

        if (settings.Mode == ProxyMode.System)
        {
            // Git for Windows libcurl/SSPI esetén a : felhasználó-jelszó pár jelzi,
            // hogy a Windows aktuális bejelentkezési hitelesítő adatait használja a
            // proxy Negotiate/NTLM hitelesítéséhez. A proxy jelszó nem kerül a logba.
            var systemHttp = GetSystemProxyUri(HttpProbeUri);
            var systemHttps = GetSystemProxyUri(HttpsProbeUri) ?? systemHttp;
            SetProxyEnvironment(
                environment,
                AddAmbientWindowsCredentials(systemHttp),
                AddAmbientWindowsCredentials(systemHttps));
            SetNoProxyEnvironment(environment, settings.NonProxyHosts);
            environment["GIT_HTTP_PROXY_AUTHMETHOD"] = "anyauth";
        }
        else if (settings.Mode == ProxyMode.Custom && !string.IsNullOrWhiteSpace(settings.Username))
        {
            environment["GIT_HTTP_PROXY_AUTHMETHOD"] = "anyauth";
        }

        return environment;
    }

    public static void MergeInto(IDictionary<string, string> target, ProxySettings settings)
    {
        foreach (var pair in CreateProcessEnvironment(settings)) target[pair.Key] = pair.Value;
    }

    public static void MergeGitInto(IDictionary<string, string> target, ProxySettings settings)
    {
        foreach (var pair in CreateGitProcessEnvironment(settings)) target[pair.Key] = pair.Value;
    }

    public static string Describe(ProxySettings settings)
    {
        var proxy = settings.Mode switch
        {
            ProxyMode.None => "Proxy nélkül",
            ProxyMode.Custom when settings.UseSameProxyForHttps => "Egyedi HTTP/HTTPS proxy: " + SanitizeProxyForLog(settings.HttpProxyUrl),
            ProxyMode.Custom => $"Egyedi proxy: HTTP={SanitizeProxyForLog(settings.HttpProxyUrl)}, HTTPS={SanitizeProxyForLog(settings.EffectiveHttpsProxyUrl)}",
            _ => "Windows rendszerproxy"
        };

        var bypass = NormalizeJavaNonProxyHosts(settings.NonProxyHosts);
        return string.IsNullOrWhiteSpace(bypass) ? proxy : $"{proxy}; kivételek={bypass}";
    }

    public static async Task TestAsync(ProxySettings settings, BootstrapperConfig config, CancellationToken ct)
    {
        using var http = CreateHttpClient(settings, TimeSpan.FromSeconds(25));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("M2M-XML-EDITOR-Online-Setup/0.9");

        using (var githubResponse = await http.GetAsync(
                   $"https://api.github.com/repos/{config.GithubOwner}/{config.GithubRepository}/releases?per_page=1",
                   HttpCompletionOption.ResponseHeadersRead,
                   ct))
        {
            githubResponse.EnsureSuccessStatusCode();
        }

        var metadataUri = $"https://api.adoptium.net/v3/assets/latest/{config.JavaMajorVersion}/hotspot?architecture=x64&image_type=jdk&os=windows&vendor=eclipse";
        using var metadataResponse = await http.GetAsync(metadataUri, HttpCompletionOption.ResponseHeadersRead, ct);
        metadataResponse.EnsureSuccessStatusCode();
        await using var metadataStream = await metadataResponse.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(metadataStream, cancellationToken: ct);
        var downloadUri = ReadTemurinPackageLink(document.RootElement)
            ?? throw new InvalidDataException("A Temurin metaadat-válasz nem tartalmaz letöltési hivatkozást.");

        using var probeRequest = new HttpRequestMessage(HttpMethod.Get, downloadUri)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        probeRequest.Headers.Range = new RangeHeaderValue(0, 0);
        probeRequest.Headers.ConnectionClose = true;
        using var binaryResponse = await http.SendAsync(probeRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        binaryResponse.EnsureSuccessStatusCode();
    }

    private static string? ReadTemurinPackageLink(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) return null;
        var first = root[0];
        if (!first.TryGetProperty("binary", out var binary)) return null;
        if (!binary.TryGetProperty("package", out var package)) return null;
        return package.TryGetProperty("link", out var link) ? link.GetString() : null;
    }

    public static void Validate(ProxySettings settings)
    {
        ValidateNonProxyHosts(settings.NonProxyHosts);
        if (settings.Mode != ProxyMode.Custom) return;
        ValidateProxyUri(settings.HttpProxyUrl, "HTTP proxy");
        if (!settings.UseSameProxyForHttps)
            ValidateProxyUri(settings.HttpsProxyUrl, "HTTPS proxy");
    }

    private static void ValidateProxyUri(string value, string label)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new InvalidOperationException($"A(z) {label} címe érvénytelen. Példa: http://proxy.szervezet.hu:8080");
        }
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException($"A(z) {label} címe ne tartalmazzon felhasználónevet vagy jelszót; ezeket a külön mezőkben add meg.");
        }
    }

    private static void ValidateNonProxyHosts(string value)
    {
        if (value.Contains('"'))
            throw new InvalidOperationException("A proxy kivétellista nem tartalmazhat idézőjelet.");
    }

    private static IWebProxy CreateCustomProxy(ProxySettings settings, IReadOnlyList<string> bypassPatterns)
    {
        Validate(settings);
        var credentials = string.IsNullOrWhiteSpace(settings.Username)
            ? null
            : new NetworkCredential(settings.Username.Trim(), settings.Password);
        return new SchemeAwareWebProxy(
            new Uri(settings.HttpProxyUrl.Trim()),
            new Uri(settings.EffectiveHttpsProxyUrl.Trim()),
            credentials,
            bypassPatterns);
    }

    private static string BuildAuthenticatedProxyUri(string proxyUrl, string username, string password)
    {
        var builder = new UriBuilder(proxyUrl.Trim());
        if (!string.IsNullOrWhiteSpace(username))
        {
            builder.UserName = username.Trim();
            builder.Password = password ?? string.Empty;
        }
        return builder.Uri.AbsoluteUri;
    }

    private static string? GetSystemProxyUri(Uri target)
    {
        try
        {
            var proxy = HttpClient.DefaultProxy.GetProxy(target);
            if (proxy is null || proxy == target) return null;
            return proxy.AbsoluteUri;
        }
        catch
        {
            return null;
        }
    }

    private static string AddAmbientWindowsCredentials(string? proxyUrl)
    {
        if (string.IsNullOrWhiteSpace(proxyUrl)) return string.Empty;
        if (!Uri.TryCreate(proxyUrl, UriKind.Absolute, out var uri)) return proxyUrl;
        var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        return $"{uri.Scheme}://:@{uri.Host}{port}";
    }

    private static void SetProxyEnvironment(IDictionary<string, string> environment, string httpProxy, string httpsProxy)
    {
        environment["HTTP_PROXY"] = httpProxy;
        environment["HTTPS_PROXY"] = httpsProxy;
        environment["http_proxy"] = httpProxy;
        environment["https_proxy"] = httpsProxy;
    }

    private static void SetNoProxyEnvironment(IDictionary<string, string> environment, string nonProxyHosts)
    {
        var noProxy = NormalizeNoProxyHosts(nonProxyHosts);
        environment["NO_PROXY"] = noProxy;
        environment["no_proxy"] = noProxy;
    }

    private static void AddMavenProxyOptions(
        IDictionary<string, string> environment,
        string? httpProxyUrl,
        string? httpsProxyUrl,
        string nonProxyHosts)
    {
        var options = new StringBuilder();
        if (Uri.TryCreate(httpProxyUrl?.Trim(), UriKind.Absolute, out var httpUri))
        {
            options.Append("-Dhttp.proxyHost=").Append(httpUri.Host)
                .Append(" -Dhttp.proxyPort=").Append(httpUri.Port);
        }
        if (Uri.TryCreate(httpsProxyUrl?.Trim(), UriKind.Absolute, out var httpsUri))
        {
            if (options.Length > 0) options.Append(' ');
            options.Append("-Dhttps.proxyHost=").Append(httpsUri.Host)
                .Append(" -Dhttps.proxyPort=").Append(httpsUri.Port);
        }

        var javaBypass = NormalizeJavaNonProxyHosts(nonProxyHosts);
        if (!string.IsNullOrWhiteSpace(javaBypass))
        {
            if (options.Length > 0) options.Append(' ');
            options.Append("-Dhttp.nonProxyHosts=\"").Append(javaBypass).Append('"');
        }

        if (options.Length == 0) return;

        var existing = Environment.GetEnvironmentVariable("MAVEN_OPTS");
        environment["MAVEN_OPTS"] = string.IsNullOrWhiteSpace(existing) ? options.ToString() : existing + " " + options;
    }

    /// <summary>
    /// Csak akkor készít ideiglenes Maven proxy settings fájlt, ha nincs felhasználói
    /// ~/.m2/settings.xml. A meglévő céges settings.xml állományt soha nem másolja,
    /// nem írja át és nem adja át felülíró -s paraméterként.
    /// </summary>
    public static string? CreateMavenSettingsFile(ProxySettings settings, string directory)
    {
        if (settings.Mode != ProxyMode.Custom) return null;
        if (File.Exists(GetUserMavenSettingsPath())) return null;

        Validate(settings);
        Directory.CreateDirectory(directory);
        foreach (var stale in Directory.GetFiles(directory, "maven-proxy-*.xml", SearchOption.TopDirectoryOnly))
        {
            try { File.Delete(stale); } catch { }
        }

        var path = Path.Combine(directory, "maven-proxy-" + Guid.NewGuid().ToString("N") + ".xml");
        XNamespace ns = "http://maven.apache.org/SETTINGS/1.0.0";
        var proxies = new XElement(ns + "proxies");
        var document = new XDocument(new XDeclaration("1.0", "UTF-8", null), new XElement(ns + "settings", proxies));
        AddMavenProxyElement(proxies, ns, "m2m-bootstrapper-https-proxy", "https", settings.EffectiveHttpsProxyUrl, settings);
        document.Save(path);
        return path;
    }

    public static string GetUserMavenSettingsPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".m2",
        "settings.xml");

    private static void AddMavenProxyElement(
        XElement proxies,
        XNamespace ns,
        string id,
        string protocol,
        string proxyUrl,
        ProxySettings settings)
    {
        var uri = new Uri(proxyUrl.Trim());
        var proxy = new XElement(ns + "proxy",
            new XElement(ns + "id", id),
            new XElement(ns + "active", "true"),
            new XElement(ns + "protocol", protocol),
            new XElement(ns + "host", uri.Host),
            new XElement(ns + "port", uri.Port));

        var bypass = NormalizeJavaNonProxyHosts(settings.NonProxyHosts);
        if (!string.IsNullOrWhiteSpace(bypass))
            proxy.Add(new XElement(ns + "nonProxyHosts", bypass));

        if (!string.IsNullOrWhiteSpace(settings.Username))
        {
            proxy.Add(new XElement(ns + "username", settings.Username.Trim()));
            proxy.Add(new XElement(ns + "password", settings.Password ?? string.Empty));
        }
        proxies.Add(proxy);
    }

    public static void DeleteMavenSettingsFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public static string NormalizeJavaNonProxyHosts(string value) =>
        string.Join('|', ParseNonProxyHosts(value));

    public static string NormalizeNoProxyHosts(string value) =>
        string.Join(',', ParseNonProxyHosts(value).Select(ToNoProxyPattern));

    private static IReadOnlyList<string> ParseNonProxyHosts(string value) => value
        .Split(new[] { '|', ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(item => !string.IsNullOrWhiteSpace(item))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static string ToNoProxyPattern(string pattern)
    {
        var trimmed = pattern.Trim();
        if (trimmed.StartsWith("*.", StringComparison.Ordinal) && trimmed.Length > 2)
            return "." + trimmed[2..];
        return trimmed;
    }

    private static bool IsHostBypassed(Uri uri, IReadOnlyList<string> patterns)
    {
        if (patterns.Count == 0) return false;
        var host = uri.Host;
        foreach (var rawPattern in patterns)
        {
            var pattern = rawPattern.Trim();
            if (pattern == "*") return true;
            if (pattern.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = pattern[1..];
                if (host.Equals(pattern[2..], StringComparison.OrdinalIgnoreCase) ||
                    host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return true;
                continue;
            }
            if (pattern.EndsWith('*') && pattern.Length > 1 &&
                host.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase))
                return true;
            if (pattern.StartsWith('*') && pattern.Length > 1 &&
                host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase))
                return true;
            if (host.Equals(pattern, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string SanitizeProxyForLog(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return "(érvénytelen cím)";
        return $"{uri.Scheme}://{uri.Host}:{uri.Port}";
    }

    private sealed class SchemeAwareWebProxy : IWebProxy
    {
        private readonly Uri _httpProxy;
        private readonly Uri _httpsProxy;
        private readonly IReadOnlyList<string> _bypassPatterns;

        public SchemeAwareWebProxy(Uri httpProxy, Uri httpsProxy, ICredentials? credentials, IReadOnlyList<string> bypassPatterns)
        {
            _httpProxy = httpProxy;
            _httpsProxy = httpsProxy;
            _bypassPatterns = bypassPatterns;
            Credentials = credentials;
        }

        public ICredentials? Credentials { get; set; }

        public Uri GetProxy(Uri destination) => IsBypassed(destination)
            ? destination
            : destination.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                ? _httpsProxy
                : _httpProxy;

        public bool IsBypassed(Uri host) => IsHostBypassed(host, _bypassPatterns);
    }

    private sealed class BypassAwareWebProxy : IWebProxy
    {
        private readonly IWebProxy _inner;
        private readonly IReadOnlyList<string> _bypassPatterns;

        public BypassAwareWebProxy(IWebProxy inner, IReadOnlyList<string> bypassPatterns, ICredentials? credentials)
        {
            _inner = inner;
            _bypassPatterns = bypassPatterns;
            Credentials = credentials;
        }

        public ICredentials? Credentials { get; set; }

        public Uri GetProxy(Uri destination) => IsBypassed(destination)
            ? destination
            : _inner.GetProxy(destination) ?? destination;

        public bool IsBypassed(Uri host) =>
            IsHostBypassed(host, _bypassPatterns) || _inner.IsBypassed(host);
    }
}
