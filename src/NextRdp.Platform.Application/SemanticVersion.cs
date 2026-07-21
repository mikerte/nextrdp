using System.Globalization;
using System.Text.RegularExpressions;

namespace NextRdp.Platform.Application;

public readonly partial record struct SemanticVersion(int Major, int Minor, int Patch, string? PreRelease = null) : IComparable<SemanticVersion>
{
    [GeneratedRegex("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static bool TryParse(string? value, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var match = Pattern().Match(value.Trim());
        if (!match.Success ||
            !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
            !int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch)) return false;

        var preRelease = match.Groups[4].Success ? match.Groups[4].Value : null;
        if (preRelease is not null && preRelease.Split('.').Any(identifier =>
                identifier.All(char.IsDigit) && identifier.Length > 1 && identifier[0] == '0')) return false;

        version = new SemanticVersion(major, minor, patch, preRelease);
        return true;
    }

    public int CompareTo(SemanticVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result != 0) return result;
        result = Minor.CompareTo(other.Minor);
        if (result != 0) return result;
        result = Patch.CompareTo(other.Patch);
        if (result != 0) return result;
        if (PreRelease is null) return other.PreRelease is null ? 0 : 1;
        if (other.PreRelease is null) return -1;

        var left = PreRelease.Split('.');
        var right = other.PreRelease.Split('.');
        for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            var leftNumeric = int.TryParse(left[index], NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
            var rightNumeric = int.TryParse(right[index], NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);
            result = leftNumeric && rightNumeric
                ? leftNumber.CompareTo(rightNumber)
                : leftNumeric ? -1
                : rightNumeric ? 1
                : string.CompareOrdinal(left[index], right[index]);
            if (result != 0) return result;
        }
        return left.Length.CompareTo(right.Length);
    }

    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() => PreRelease is null
        ? $"{Major}.{Minor}.{Patch}"
        : $"{Major}.{Minor}.{Patch}-{PreRelease}";
}
