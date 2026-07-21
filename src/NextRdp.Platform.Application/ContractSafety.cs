using System.Reflection;

namespace NextRdp.Platform.Application;

public static class ContractSafety
{
    private static readonly string[] ForbiddenFragments =
    [
        "password",
        "credentialblob",
        "privatekey",
        "accesstoken",
        "refreshtoken"
    ];

    public static IReadOnlyList<string> FindForbiddenPublicMembers(params Type[] rootTypes)
    {
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<Type>();
        foreach (var rootType in rootTypes) Visit(rootType, rootType.Name, visited, findings);
        return findings.ToArray();
    }

    private static void Visit(Type type, string path, ISet<Type> visited, ISet<string> findings)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (IsTerminal(type) || !visited.Add(type)) return;

        if (type.IsArray)
        {
            Visit(type.GetElementType()!, path + "[]", visited, findings);
            return;
        }

        if (type.IsGenericType)
            foreach (var argument in type.GetGenericArguments()) Visit(argument, path + "<>" + argument.Name, visited, findings);

        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            var memberPath = path + "." + property.Name;
            if (ForbiddenFragments.Any(fragment => property.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase))) findings.Add(memberPath);
            Visit(property.PropertyType, memberPath, visited, findings);
        }
    }

    private static bool IsTerminal(Type type) =>
        type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(Guid) ||
        type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan) || type == typeof(Uri) ||
        type.Namespace?.StartsWith("System.Text.Json", StringComparison.Ordinal) == true;
}
