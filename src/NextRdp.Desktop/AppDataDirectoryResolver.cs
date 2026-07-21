using System.IO;

namespace NextRdp.Desktop;

internal static class AppDataDirectoryResolver
{
    public const string TestOverrideEnvironmentVariable = "NEXTRDP_TEST_DATA_DIRECTORY";

    public static string Resolve(string localApplicationData, string? testOverride)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationData);
        if (string.IsNullOrWhiteSpace(testOverride)) return Path.Combine(localApplicationData, "NextRDP");
        if (!Path.IsPathFullyQualified(testOverride))
            throw new InvalidOperationException($"{TestOverrideEnvironmentVariable} must contain a fully qualified path.");
        return Path.GetFullPath(testOverride);
    }
}
