namespace M2M.XmlEditor.Bootstrapper.Services;

public sealed class SemVer : IComparable<SemVer>
{
    public long Major { get; }
    public long Minor { get; }
    public long Patch { get; }
    public string? PreRelease { get; }
    public string Normalized => $"{Major}.{Minor}.{Patch}{(string.IsNullOrEmpty(PreRelease) ? string.Empty : "-" + PreRelease)}";

    private SemVer(long major, long minor, long patch, string? preRelease)
    {
        Major = major; Minor = minor; Patch = patch; PreRelease = preRelease;
    }

    public static bool TryParse(string? value, out SemVer? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        if (text.StartsWith('v')) text = text[1..];
        var plus = text.IndexOf('+');
        if (plus >= 0) text = text[..plus];
        var dash = text.IndexOf('-');
        var core = dash >= 0 ? text[..dash] : text;
        var pre = dash >= 0 ? text[(dash + 1)..] : null;
        var parts = core.Split('.');
        if (parts.Length != 3 || !long.TryParse(parts[0], out var major) || !long.TryParse(parts[1], out var minor) || !long.TryParse(parts[2], out var patch)) return false;
        if (major < 0 || minor < 0 || patch < 0) return false;
        if (pre is { Length: 0 }) return false;
        result = new SemVer(major, minor, patch, pre);
        return true;
    }

    public int CompareTo(SemVer? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major); if (c != 0) return c;
        c = Minor.CompareTo(other.Minor); if (c != 0) return c;
        c = Patch.CompareTo(other.Patch); if (c != 0) return c;
        if (PreRelease is null && other.PreRelease is null) return 0;
        if (PreRelease is null) return 1;
        if (other.PreRelease is null) return -1;
        var a = PreRelease.Split('.'); var b = other.PreRelease.Split('.');
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            if (i >= a.Length) return -1;
            if (i >= b.Length) return 1;
            var an = long.TryParse(a[i], out var ai); var bn = long.TryParse(b[i], out var bi);
            if (an && bn) { c = ai.CompareTo(bi); if (c != 0) return c; continue; }
            if (an != bn) return an ? -1 : 1;
            c = string.CompareOrdinal(a[i], b[i]); if (c != 0) return c;
        }
        return 0;
    }
}
