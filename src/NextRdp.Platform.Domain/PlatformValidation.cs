using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace NextRdp.Platform.Domain;

public static partial class PlatformValidation
{
    public static IReadOnlyList<string> ValidateName(string value, string label, int maximumLength = 200)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(value)) errors.Add($"{label} is required.");
        else if (value.Trim().Length > maximumLength) errors.Add($"{label} cannot exceed {maximumLength.ToString(CultureInfo.InvariantCulture)} characters.");
        else if (value.Any(char.IsControl)) errors.Add($"{label} cannot contain control characters.");
        return errors;
    }

    public static bool IsValidSlug(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 63 && SlugPattern().IsMatch(value);

    public static bool IsValidHost(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 255 ||
            value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)) ||
            value.IndexOfAny(['/', '\\', '[', ']']) >= 0)
            return false;

        if (IPAddress.TryParse(value, out _)) return true;
        if (value.Contains(':', StringComparison.Ordinal)) return false;

        var host = value.TrimEnd('.');
        if (host.Length == 0) return false;
        return host.Split('.').All(label =>
            label.Length is >= 1 and <= 63 &&
            label[0] != '-' && label[^1] != '-' &&
            label.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'));
    }

    public static bool IsSafeHttpsLink(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrEmpty(uri.UserInfo) &&
            uri.HostNameType is not UriHostNameType.Unknown;
    }

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();
}
