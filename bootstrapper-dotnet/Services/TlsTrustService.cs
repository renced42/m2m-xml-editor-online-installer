using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using M2M.XmlEditor.Bootstrapper.Models;

namespace M2M.XmlEditor.Bootstrapper.Services;

/// <summary>
/// A Mavenhez szükséges, felhasználó által elfogadott TLS tanúsítványokat kezeli.
/// Nem módosítja sem az operációs rendszer tanúsítványtárát, sem a letöltött JDK
/// eredeti cacerts állományát. A bootstrapper saját truststore-másolatot használ.
/// </summary>
public sealed class TlsTrustService
{
    private const string TrustStorePassword = "changeit";
    private readonly ProcessService _process;
    private readonly LogService _log;
    private readonly string _trustDirectory;
    private readonly string _decisionFile;

    public TlsTrustService(AppPaths paths, ProcessService process, LogService log)
    {
        _process = process;
        _log = log;
        _trustDirectory = Path.Combine(paths.Runtime, "truststore");
        _decisionFile = Path.Combine(_trustDirectory, "trusted-certificates.json");
    }

    public async Task<TlsServerCertificateInfo> FetchServerCertificateAsync(string host, int port, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, ct);

        X509Certificate2? captured = null;
        using var ssl = new SslStream(
            tcp.GetStream(),
            leaveInnerStreamOpen: false,
            (_, certificate, _, _) =>
            {
                if (certificate is not null)
                    captured = new X509Certificate2(certificate.Export(X509ContentType.Cert));
                return true; // Csak a tanúsítvány lekéréséhez; alkalmazásadat nem kerül átvitelre.
            });

        var options = new SslClientAuthenticationOptions
        {
            TargetHost = host,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        };
        await ssl.AuthenticateAsClientAsync(options, ct);

        if (captured is null)
            throw new InvalidOperationException($"A(z) {host}:{port} végpont nem adott TLS tanúsítványt.");

        using (captured)
        {
            return new TlsServerCertificateInfo
            {
                Host = host,
                Port = port,
                Subject = captured.Subject,
                Issuer = captured.Issuer,
                NotBefore = captured.NotBefore,
                NotAfter = captured.NotAfter,
                Sha256Fingerprint = FormatFingerprint(captured.GetCertHash(HashAlgorithmName.SHA256)),
                CertificateData = captured.Export(X509ContentType.Cert)
            };
        }
    }

    public bool IsPreviouslyTrusted(TlsServerCertificateInfo certificate)
    {
        return LoadDecisions().Any(item =>
            item.Host.Equals(certificate.Host, StringComparison.OrdinalIgnoreCase) &&
            item.Port == certificate.Port &&
            item.Sha256Fingerprint.Equals(certificate.Sha256Fingerprint, StringComparison.OrdinalIgnoreCase));
    }

    public void RememberTrust(TlsServerCertificateInfo certificate)
    {
        Directory.CreateDirectory(_trustDirectory);
        var decisions = LoadDecisions();
        decisions.RemoveAll(item =>
            item.Host.Equals(certificate.Host, StringComparison.OrdinalIgnoreCase) &&
            item.Port == certificate.Port &&
            !item.Sha256Fingerprint.Equals(certificate.Sha256Fingerprint, StringComparison.OrdinalIgnoreCase));

        if (!decisions.Any(item =>
                item.Host.Equals(certificate.Host, StringComparison.OrdinalIgnoreCase) &&
                item.Port == certificate.Port &&
                item.Sha256Fingerprint.Equals(certificate.Sha256Fingerprint, StringComparison.OrdinalIgnoreCase)))
        {
            decisions.Add(new TrustedCertificateDecision
            {
                Host = certificate.Host,
                Port = certificate.Port,
                Sha256Fingerprint = certificate.Sha256Fingerprint,
                Subject = certificate.Subject,
                Issuer = certificate.Issuer,
                TrustedAtUtc = DateTime.UtcNow
            });
        }

        var temp = _decisionFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(decisions, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _decisionFile, true);
    }

    public async Task<string> EnsureTrustStoreAsync(
        TlsServerCertificateInfo certificate,
        string jdkHome,
        CancellationToken ct)
    {
        Directory.CreateDirectory(_trustDirectory);
        var sourceTrustStore = Path.Combine(jdkHome, "lib", "security", "cacerts");
        if (!File.Exists(sourceTrustStore))
            throw new FileNotFoundException("A JDK alapértelmezett cacerts truststore-ja nem található.", sourceTrustStore);

        var fingerprintId = ShortFingerprint(certificate.Sha256Fingerprint);
        var targetTrustStore = Path.Combine(_trustDirectory, $"maven-cacerts-{fingerprintId}");
        var markerFile = targetTrustStore + ".ready";
        if (File.Exists(targetTrustStore) && File.Exists(markerFile))
        {
            _log.Write($"Korábban elkészített fingerprint-specifikus Maven truststore használata: {targetTrustStore}");
            return targetTrustStore;
        }

        File.Copy(sourceTrustStore, targetTrustStore, overwrite: true);
        _log.Write($"Bootstrapper Maven truststore létrehozva: {targetTrustStore}");

        var certFile = Path.Combine(_trustDirectory, $"trusted-{SanitizeFileName(certificate.Host)}-{fingerprintId}.cer");
        await File.WriteAllBytesAsync(certFile, certificate.CertificateData, ct);

        var keytool = ResolveKeytool(jdkHome);
        var alias = $"m2m-bootstrapper-{SanitizeAlias(certificate.Host)}-{fingerprintId}";
        await _process.RunAsync(
            keytool,
            new[]
            {
                "-importcert", "-noprompt", "-trustcacerts",
                "-alias", alias,
                "-file", certFile,
                "-keystore", targetTrustStore,
                "-storetype", "JKS",
                "-storepass", TrustStorePassword
            },
            _trustDirectory,
            null,
            null,
            ct);
        await File.WriteAllTextAsync(markerFile, certificate.Sha256Fingerprint, ct);

        _log.Write($"TLS tanúsítvány használatra kész: endpoint={certificate.Endpoint}, SHA-256={certificate.Sha256Fingerprint}, truststore={targetTrustStore}");
        return targetTrustStore;
    }

    public static void ApplyTrustStore(IDictionary<string, string> environment, string trustStorePath)
    {
        var options = $"-Djavax.net.ssl.trustStore=\"{trustStorePath}\" -Djavax.net.ssl.trustStoreType=JKS -Djavax.net.ssl.trustStorePassword={TrustStorePassword}";
        if (environment.TryGetValue("MAVEN_OPTS", out var existing) && !string.IsNullOrWhiteSpace(existing))
            environment["MAVEN_OPTS"] = existing + " " + options;
        else
            environment["MAVEN_OPTS"] = options;
    }

    private List<TrustedCertificateDecision> LoadDecisions()
    {
        try
        {
            if (!File.Exists(_decisionFile)) return new List<TrustedCertificateDecision>();
            return JsonSerializer.Deserialize<List<TrustedCertificateDecision>>(File.ReadAllText(_decisionFile))
                   ?? new List<TrustedCertificateDecision>();
        }
        catch (Exception ex)
        {
            _log.Write($"A mentett TLS megbízhatósági döntések nem olvashatók: {ex.Message}");
            return new List<TrustedCertificateDecision>();
        }
    }

    private static string ResolveKeytool(string jdkHome)
    {
        var fileName = OperatingSystem.IsWindows() ? "keytool.exe" : "keytool";
        var path = Path.Combine(jdkHome, "bin", fileName);
        if (!File.Exists(path)) throw new FileNotFoundException("A JDK keytool programja nem található.", path);
        return path;
    }

    private static string FormatFingerprint(byte[] bytes) => string.Join(':', bytes.Select(b => b.ToString("X2")));
    private static string ShortFingerprint(string fingerprint) => fingerprint.Replace(":", string.Empty, StringComparison.Ordinal)[..16];
    private static string SanitizeAlias(string value) => new(value.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray());
    private static string SanitizeFileName(string value) => new(value.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray());

    private sealed class TrustedCertificateDecision
    {
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; }
        public string Sha256Fingerprint { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public string Issuer { get; set; } = string.Empty;
        public DateTime TrustedAtUtc { get; set; }
    }
}
