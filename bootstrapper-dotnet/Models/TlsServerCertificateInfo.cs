namespace M2M.XmlEditor.Bootstrapper.Models;

public sealed class TlsServerCertificateInfo
{
    public required string Host { get; init; }
    public int Port { get; init; } = 443;
    public required string Subject { get; init; }
    public required string Issuer { get; init; }
    public DateTime NotBefore { get; init; }
    public DateTime NotAfter { get; init; }
    public required string Sha256Fingerprint { get; init; }
    public required byte[] CertificateData { get; init; }

    public string Endpoint => $"{Host}:{Port}";
}
