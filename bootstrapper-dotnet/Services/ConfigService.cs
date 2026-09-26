using System.Reflection;
using System.Text.Json;
using M2M.XmlEditor.Bootstrapper.Models;

namespace M2M.XmlEditor.Bootstrapper.Services;

public static class ConfigService
{
    private const string ConfigResource = "M2M.XmlEditor.Bootstrapper.Embedded.bootstrapper.config.json";

    public static BootstrapperConfig Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ConfigResource)
            ?? throw new InvalidOperationException($"Hiányzik a beágyazott bootstrapper konfiguráció: {ConfigResource}");
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        return JsonSerializer.Deserialize<BootstrapperConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("A beágyazott bootstrapper konfiguráció nem olvasható.");
    }
}
