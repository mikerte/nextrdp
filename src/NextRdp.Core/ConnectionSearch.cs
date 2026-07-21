namespace NextRdp.Core;

public static class ConnectionSearch
{
    public static bool Matches(ConnectionProfile profile, string query)
    {
        query = query.Trim();
        if (query.Length == 0) return true;
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var fields = new[] { profile.Name, profile.Host, profile.Environment.ToString(), profile.Notes, profile.SearchableMetadata }
            .Concat(profile.Tags)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        return terms.All(term => fields.Any(field => FuzzyContains(field, term)));
    }

    internal static bool FuzzyContains(string value, string term)
    {
        if (value.Contains(term, StringComparison.OrdinalIgnoreCase)) return true;
        var valueIndex = 0;
        foreach (var expected in term)
        {
            while (valueIndex < value.Length && char.ToUpperInvariant(value[valueIndex]) != char.ToUpperInvariant(expected)) valueIndex++;
            if (valueIndex == value.Length) return false;
            valueIndex++;
        }
        return true;
    }
}
