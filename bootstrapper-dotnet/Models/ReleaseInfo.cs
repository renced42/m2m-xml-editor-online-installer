namespace M2M.XmlEditor.Bootstrapper.Models;

public sealed record ReleaseInfo(string TagName, string Name, bool Prerelease, bool Draft, DateTimeOffset? PublishedAt)
{
    public string DisplayName => Prerelease ? $"{TagName} (prerelease)" : $"{TagName} (stable)";
}
