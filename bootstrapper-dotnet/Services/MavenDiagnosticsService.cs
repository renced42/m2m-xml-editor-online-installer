using System.Text.RegularExpressions;
using System.Xml.Linq;
using M2M.XmlEditor.Bootstrapper.Models;

namespace M2M.XmlEditor.Bootstrapper.Services;

/// <summary>
/// A Maven hálózati útvonalának diagnosztikáját készíti el anélkül, hogy a
/// felhasználói settings.xml állományt módosítaná vagy titkos adatot naplózna.
/// </summary>
public static class MavenDiagnosticsService
{
    public static void WriteDiagnostics(
        LogService log,
        string jdkHome,
        ProxySettings proxySettings,
        IDictionary<string, string> environment)
    {
        log.Write("--- Maven hálózati diagnosztika ---");
        log.Write($"Maven proxy mód: {ProxyNetworkService.Describe(proxySettings)}");

        var javaNonProxyHosts = ProxyNetworkService.NormalizeJavaNonProxyHosts(proxySettings.NonProxyHosts);
        var noProxy = ProxyNetworkService.NormalizeNoProxyHosts(proxySettings.NonProxyHosts);
        log.Write($"Java http.nonProxyHosts: {DisplayValue(javaNonProxyHosts)}");
        log.Write($"NO_PROXY/no_proxy: {DisplayValue(noProxy)}");

        if (environment.TryGetValue("MAVEN_OPTS", out var mavenOpts) && !string.IsNullOrWhiteSpace(mavenOpts))
            log.Write($"MAVEN_OPTS (titokmentes): {SanitizeMavenOpts(mavenOpts)}");
        else
            log.Write("MAVEN_OPTS: nincs beállítva");

        LogEnvironmentProxy(log, environment, "HTTP_PROXY");
        LogEnvironmentProxy(log, environment, "HTTPS_PROXY");

        var trustStore = Path.Combine(jdkHome, "lib", "security", "cacerts");
        log.Write($"Java alapértelmezett truststore: {trustStore} (létezik={File.Exists(trustStore)})");
        log.Write(ContainsTrustStoreOverride(mavenOpts)
            ? "Java truststore felülírás: MAVEN_OPTS tartalmaz javax.net.ssl.trustStore beállítást."
            : "Java truststore felülírás: nincs; a bootstrapper JDK alapértelmezett truststore-ja használatos.");

        var settingsPath = ProxyNetworkService.GetUserMavenSettingsPath();
        if (!File.Exists(settingsPath))
        {
            log.Write("Maven settings hálózati diagnosztika: nincs felhasználói ~/.m2/settings.xml.");
            log.Write("--- Maven hálózati diagnosztika vége ---");
            return;
        }

        try
        {
            var document = XDocument.Load(settingsPath, LoadOptions.None);
            LogSettingsProxies(log, document);
            LogRemoteEndpoints(log, document, proxySettings, environment);
        }
        catch (Exception ex)
        {
            log.Write($"Maven settings hálózati diagnosztika nem olvasható: {ex.Message}");
        }

        log.Write("--- Maven hálózati diagnosztika vége ---");
    }

    private static void LogSettingsProxies(LogService log, XDocument document)
    {
        var proxies = Elements(document, "proxy").ToArray();
        if (proxies.Length == 0)
        {
            log.Write("Maven settings.xml proxy bejegyzés: nincs.");
            return;
        }

        foreach (var proxy in proxies)
        {
            var id = ChildValue(proxy, "id");
            var active = ChildValue(proxy, "active");
            var protocol = ChildValue(proxy, "protocol");
            var host = ChildValue(proxy, "host");
            var port = ChildValue(proxy, "port");
            var bypass = ChildValue(proxy, "nonProxyHosts");
            var hasUser = !string.IsNullOrWhiteSpace(ChildValue(proxy, "username"));
            var hasPassword = !string.IsNullOrWhiteSpace(ChildValue(proxy, "password"));
            log.Write(
                $"Maven settings proxy: id={DisplayValue(id)}, active={DisplayValue(active)}, " +
                $"protocol={DisplayValue(protocol)}, host={DisplayValue(host)}, port={DisplayValue(port)}, " +
                $"nonProxyHosts={DisplayValue(bypass)}, username={(hasUser ? "megadva" : "nincs")}, " +
                $"password={(hasPassword ? "megadva" : "nincs")}");
        }
    }

    private static void LogRemoteEndpoints(
        LogService log,
        XDocument document,
        ProxySettings proxySettings,
        IDictionary<string, string> environment)
    {
        var endpoints = new List<(string Kind, string Id, string Url)>();

        foreach (var mirror in Elements(document, "mirror"))
            AddEndpoint(endpoints, "mirror", ChildValue(mirror, "id"), ChildValue(mirror, "url"));
        foreach (var repository in Elements(document, "repository"))
            AddEndpoint(endpoints, "repository", ChildValue(repository, "id"), ChildValue(repository, "url"));
        foreach (var repository in Elements(document, "pluginRepository"))
            AddEndpoint(endpoints, "pluginRepository", ChildValue(repository, "id"), ChildValue(repository, "url"));

        if (endpoints.Count == 0)
        {
            log.Write("Maven settings.xml távoli repository/mirror URL: nincs explicit bejegyzés.");
            return;
        }

        foreach (var endpoint in endpoints
                     .DistinctBy(x => $"{x.Kind}|{x.Id}|{x.Url}", StringComparer.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(endpoint.Url, UriKind.Absolute, out var uri))
            {
                log.Write($"Maven {endpoint.Kind}: id={DisplayValue(endpoint.Id)}, url={endpoint.Url}, útvonal=ISMERETLEN (érvénytelen URL)");
                continue;
            }

            var bypassPattern = FindMatchingBypassPattern(uri.Host, proxySettings.NonProxyHosts);
            if (proxySettings.Mode == ProxyMode.None)
            {
                log.Write($"Maven {endpoint.Kind}: id={DisplayValue(endpoint.Id)}, url={uri}, host={uri.Host}, útvonal=DIRECT (proxy kikapcsolva)");
            }
            else if (bypassPattern is not null)
            {
                log.Write($"Maven {endpoint.Kind}: id={DisplayValue(endpoint.Id)}, url={uri}, host={uri.Host}, útvonal=DIRECT (nonProxyHosts találat: {bypassPattern})");
            }
            else
            {
                var proxy = GetProxyForScheme(uri.Scheme, environment);
                log.Write($"Maven {endpoint.Kind}: id={DisplayValue(endpoint.Id)}, url={uri}, host={uri.Host}, útvonal=PROXY ({proxy})");
            }
        }
    }

    private static string GetProxyForScheme(string scheme, IDictionary<string, string> environment)
    {
        var key = scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ? "HTTP_PROXY" : "HTTPS_PROXY";
        if (!environment.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            return "nincs proxy URL a folyamatkörnyezetben";
        return SanitizeProxyUri(value);
    }

    private static string? FindMatchingBypassPattern(string host, string rawPatterns)
    {
        foreach (var pattern in SplitPatterns(rawPatterns))
        {
            var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
            if (Regex.IsMatch(host, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return pattern;
        }
        return null;
    }

    private static IEnumerable<string> SplitPatterns(string value) => value
        .Split(new[] { '|', ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(x => !string.IsNullOrWhiteSpace(x));

    private static IEnumerable<XElement> Elements(XDocument document, string localName) =>
        document.Descendants().Where(x => x.Name.LocalName.Equals(localName, StringComparison.Ordinal));

    private static string ChildValue(XElement element, string localName) =>
        element.Elements().FirstOrDefault(x => x.Name.LocalName.Equals(localName, StringComparison.Ordinal))?.Value.Trim() ?? string.Empty;

    private static void AddEndpoint(List<(string Kind, string Id, string Url)> target, string kind, string id, string url)
    {
        if (!string.IsNullOrWhiteSpace(url)) target.Add((kind, id, url));
    }

    private static void LogEnvironmentProxy(LogService log, IDictionary<string, string> environment, string key)
    {
        var value = environment.TryGetValue(key, out var raw) ? raw : string.Empty;
        log.Write($"{key}: {(string.IsNullOrWhiteSpace(value) ? "nincs" : SanitizeProxyUri(value))}");
    }

    private static string SanitizeProxyUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return "beállítva (URL nem értelmezhető)";
        var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        return $"{uri.Scheme}://{uri.Host}{port}";
    }

    private static string SanitizeMavenOpts(string value)
    {
        var result = Regex.Replace(value, "(?i)(-D[^\\s=]*(?:password|passwd|secret|token)[^\\s=]*=)(?:\"[^\"]*\"|\\S+)", "$1***");
        result = Regex.Replace(result, "(?i)(https?://)[^\\s/@:]+:[^\\s/@]*@", "$1***:***@");
        return result;
    }

    private static bool ContainsTrustStoreOverride(string? mavenOpts) =>
        !string.IsNullOrWhiteSpace(mavenOpts) &&
        mavenOpts.Contains("javax.net.ssl.trustStore", StringComparison.OrdinalIgnoreCase);

    private static string DisplayValue(string? value) => string.IsNullOrWhiteSpace(value) ? "nincs" : value;
}
