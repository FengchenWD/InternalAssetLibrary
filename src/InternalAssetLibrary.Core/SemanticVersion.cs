namespace InternalAssetLibrary.Core;

public readonly record struct SemanticVersion : IComparable<SemanticVersion>
{
    private readonly string[] _preReleaseIdentifiers;

    private SemanticVersion(int major, int minor, int patch, string[] preReleaseIdentifiers)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        _preReleaseIdentifiers = preReleaseIdentifiers;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public bool IsPreRelease => PreReleaseIdentifiers.Count > 0;
    public IReadOnlyList<string> PreReleaseIdentifiers => _preReleaseIdentifiers ?? [];

    public static bool TryParse(string? value, out SemanticVersion version)
    {
        version = default;
        var candidate = value?.Trim();
        if (string.IsNullOrEmpty(candidate) || candidate.Length > 128 || candidate.Contains('+'))
        {
            return false;
        }

        var dash = candidate.IndexOf('-');
        var core = dash < 0 ? candidate : candidate[..dash];
        var preRelease = dash < 0 ? [] : candidate[(dash + 1)..].Split('.');
        var parts = core.Split('.');
        if (parts.Length != 3 ||
            !TryParseNumericIdentifier(parts[0], out var major) ||
            !TryParseNumericIdentifier(parts[1], out var minor) ||
            !TryParseNumericIdentifier(parts[2], out var patch) ||
            preRelease.Any(identifier => !IsValidPreReleaseIdentifier(identifier)))
        {
            return false;
        }

        version = new SemanticVersion(major, minor, patch, preRelease);
        return true;
    }

    public static SemanticVersion Parse(string value) => TryParse(value, out var version)
        ? version
        : throw new FormatException($"'{value}' is not a supported semantic version.");

    public int CompareTo(SemanticVersion other)
    {
        var core = Major.CompareTo(other.Major);
        if (core == 0) core = Minor.CompareTo(other.Minor);
        if (core == 0) core = Patch.CompareTo(other.Patch);
        if (core != 0) return core;

        var left = PreReleaseIdentifiers;
        var right = other.PreReleaseIdentifiers;
        if (left.Count == 0 || right.Count == 0)
        {
            return left.Count == right.Count ? 0 : left.Count == 0 ? 1 : -1;
        }

        for (var index = 0; index < Math.Min(left.Count, right.Count); index++)
        {
            var identifierComparison = CompareIdentifier(left[index], right[index]);
            if (identifierComparison != 0)
            {
                return identifierComparison;
            }
        }

        return left.Count.CompareTo(right.Count);
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = int.TryParse(left, out var leftNumber);
        var rightNumeric = int.TryParse(right, out var rightNumber);
        if (leftNumeric && rightNumeric) return leftNumber.CompareTo(rightNumber);
        if (leftNumeric != rightNumeric) return leftNumeric ? -1 : 1;
        return string.CompareOrdinal(left, right);
    }

    private static bool TryParseNumericIdentifier(string value, out int result)
    {
        result = 0;
        return value.Length > 0 &&
               (value.Length == 1 || value[0] != '0') &&
               value.All(char.IsAsciiDigit) &&
               int.TryParse(value, out result);
    }

    private static bool IsValidPreReleaseIdentifier(string value) =>
        value.Length > 0 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-') &&
        (!value.All(char.IsAsciiDigit) || value.Length == 1 || value[0] != '0');
}
