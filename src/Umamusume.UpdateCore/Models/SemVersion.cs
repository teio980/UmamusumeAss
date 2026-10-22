namespace Umamusume.UpdateCore;

public readonly record struct SemVersion(int Major, int Minor, int Patch) : IComparable<SemVersion>
{
    public static bool TryParse(string? text, out SemVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
            value = value[1..];
        var parts = value.Split('.');
        if (parts.Length != 3)
            return false;
        if (!int.TryParse(parts[0], out var parsedMajor)
            || !int.TryParse(parts[1], out var parsedMinor)
            || !int.TryParse(parts[2], out var parsedPatch)
            || parsedMajor < 0 || parsedMinor < 0 || parsedPatch < 0)
            return false;
        version = new SemVersion(parsedMajor, parsedMinor, parsedPatch);
        return true;
    }

    public int CompareTo(SemVersion other) =>
        Major != other.Major ? Major.CompareTo(other.Major)
        : Minor != other.Minor ? Minor.CompareTo(other.Minor)
        : Patch.CompareTo(other.Patch);

    public override string ToString() => $"{Major}.{Minor}.{Patch}";

    public static bool operator >(SemVersion left, SemVersion right) => left.CompareTo(right) > 0;
    public static bool operator <(SemVersion left, SemVersion right) => left.CompareTo(right) < 0;
    public static bool operator <=(SemVersion left, SemVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(SemVersion left, SemVersion right) => left.CompareTo(right) >= 0;
}
