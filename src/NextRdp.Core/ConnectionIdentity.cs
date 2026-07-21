using System.Net;

namespace NextRdp.Core;

public static class ConnectionIdentity
{
    public static string CreateKey(string host, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var normalizedHost = host.Trim();
        if (IPAddress.TryParse(normalizedHost, out var address))
            normalizedHost = address.ToString();
        else
            normalizedHost = normalizedHost.TrimEnd('.').ToLowerInvariant();
        return $"[{normalizedHost}]:{port}";
    }
}
