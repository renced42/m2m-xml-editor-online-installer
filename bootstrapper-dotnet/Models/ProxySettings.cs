namespace M2M.XmlEditor.Bootstrapper.Models;

public sealed class ProxySettings
{
    public ProxyMode Mode { get; set; } = ProxyMode.System;
    public string HttpProxyUrl { get; set; } = string.Empty;
    public string HttpsProxyUrl { get; set; } = string.Empty;
    public bool UseSameProxyForHttps { get; set; } = true;
    public string NonProxyHosts { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool Remember { get; set; } = true;

    public ProxySettings Clone() => new()
    {
        Mode = Mode,
        HttpProxyUrl = HttpProxyUrl,
        HttpsProxyUrl = HttpsProxyUrl,
        UseSameProxyForHttps = UseSameProxyForHttps,
        NonProxyHosts = NonProxyHosts,
        Username = Username,
        Password = Password,
        Remember = Remember
    };

    public string EffectiveHttpsProxyUrl => UseSameProxyForHttps ? HttpProxyUrl : HttpsProxyUrl;
}
