using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Xml;
using System.Xml.Linq;
using NextRdp.Core;

namespace NextRdp.Infrastructure;

public sealed class FileImportService : IImportService
{
    private const long MaxImportBytes = 2 * 1024 * 1024;
    private const int MaxRdgElementDepth = 64;
    private const int MaxImportCandidates = 2000;
    private static readonly HashSet<string> RdpStringKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "full address", "username", "domain", "drivestoredirect"
    };
    private static readonly HashSet<string> RdpIntegerKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "server port", "enablecredsspsupport", "redirectclipboard", "redirectdrives", "redirectprinters",
        "audiomode", "smart sizing", "session bpp", "autoreconnection enabled", "autoreconnect max retries"
    };

    public async Task<ImportPreview> PreviewAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var file = new FileInfo(path);
        if (!file.Exists) return new(path, [], ["Import file does not exist."], 0);
        if (file.Length > MaxImportBytes) return new(path, [], ["Import file exceeds the 2 MB Phase 1 limit."], 0);

        try
        {
            return file.Extension.ToLowerInvariant() switch
            {
                ".rdp" => await ParseRdpAsync(path, cancellationToken),
                ".rdg" => await ParseRdgAsync(path, cancellationToken),
                _ => new(path, [], ["Only .rdp and .rdg files are supported in Phase 1."], 0)
            };
        }
        catch (XmlException)
        {
            return new(path, [], ["The .rdg file is malformed or contains prohibited XML content."], 0);
        }
    }

    private static async Task<ImportPreview> ParseRdpAsync(string path, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var discarded = 0;
        foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken))
        {
            if (line.Length > 16_384) continue;
            var first = line.IndexOf(':');
            var second = first < 0 ? -1 : line.IndexOf(':', first + 1);
            if (first <= 0 || second <= first) continue;
            var key = line[..first].Trim();
            if (IsSecretKey(key)) { discarded++; continue; }
            var valueType = line[(first + 1)..second].Trim();
            if (!HasExpectedRdpValueType(key, valueType)) continue;
            values[key] = line[(second + 1)..].Trim();
        }

        var address = values.GetValueOrDefault("full address", string.Empty);
        var (host, embeddedPort) = SplitHostAndPort(address);
        var port = embeddedPort ?? ReadInt(values, "server port", 3389);
        if (string.IsNullOrWhiteSpace(host)) return new(path, [], ["The .rdp file has no valid full address."], discarded);
        var username = BuildUsernameHint(values.GetValueOrDefault("username"), values.GetValueOrDefault("domain"));
        var settings = new RdpSettings
        {
            RequireNetworkLevelAuthentication = ReadBoolean(values, "enablecredsspsupport", true),
            RedirectClipboard = ReadBoolean(values, "redirectclipboard", true),
            RedirectDrives = ReadDriveRedirection(values),
            RedirectPrinters = ReadBoolean(values, "redirectprinters", false),
            RedirectAudio = ReadInt(values, "audiomode", 0) == 0,
            SmartSizing = ReadBoolean(values, "smart sizing", true),
            ColorDepth = ReadInt(values, "session bpp", 32),
            ReconnectAttempts = ReadBoolean(values, "autoreconnection enabled", true)
                ? ReadInt(values, "autoreconnect max retries", 5)
                : 0
        };
        var fileName = Path.GetFileNameWithoutExtension(path).Trim();
        var candidateName = string.IsNullOrWhiteSpace(fileName) ? host : fileName;
        var warnings = ValidateCandidate(candidateName, host, port, username, [], settings);
        var candidate = new ImportCandidate(path, candidateName, host, port,
            username, [], settings, warnings);
        return new(path, [candidate], [], discarded);
    }

    private static async Task<ImportPreview> ParseRdgAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        var readerSettings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxImportBytes
        };
        using var reader = XmlReader.Create(stream, readerSettings);
        var document = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken);
        var candidates = new List<ImportCandidate>();
        var discarded = document.Descendants().Count(x => IsSecretKey(x.Name.LocalName));
        var root = document.Root;
        if (root is null) return new(path, [], ["The .rdg document has no root element."], discarded);
        if (!root.Name.LocalName.Equals("RDCMan", StringComparison.OrdinalIgnoreCase))
            return new(path, [], ["The .rdg document root must be RDCMan."], discarded);
        if (GetMaximumElementDepth(root) > MaxRdgElementDepth)
            return new(path, [], [$"The .rdg document exceeds the maximum XML nesting depth of {MaxRdgElementDepth}."], discarded);
        var serverCount = root.Descendants().Count(x => x.Name.LocalName.Equals("server", StringComparison.OrdinalIgnoreCase));
        if (serverCount > MaxImportCandidates)
            return new(path, [], [$"The .rdg document contains {serverCount} servers; the Phase 1 limit is {MaxImportCandidates} per import."], discarded);
        WalkRdg(root, [], path, candidates);
        var errors = candidates.Count == 0 ? new[] { "The .rdg file contains no valid server entries." } : [];
        return new(path, candidates, errors, discarded);
    }

    private static void WalkRdg(XElement node, IReadOnlyList<string> folderPath, string source, List<ImportCandidate> candidates)
    {
        foreach (var child in node.Elements())
        {
            if (child.Name.LocalName.Equals("group", StringComparison.OrdinalIgnoreCase))
            {
                var groupName = Property(child, "displayName") ?? Property(child, "name") ?? "Imported group";
                WalkRdg(child, [.. folderPath, groupName], source, candidates);
            }
            else if (child.Name.LocalName.Equals("server", StringComparison.OrdinalIgnoreCase))
            {
                var host = Property(child, "name") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(host)) continue;
                var displayName = Property(child, "displayName") ?? host;
                var port = int.TryParse(Property(child, "port"), out var parsedPort) ? parsedPort : 3389;
                var username = BuildUsernameHint(Property(child, "userName"), Property(child, "domain"));
                var settings = new RdpSettings();
                candidates.Add(new(source, displayName, host, port, username, folderPath, settings,
                    ValidateCandidate(displayName, host, port, username, folderPath, settings)));
            }
            else
            {
                WalkRdg(child, folderPath, source, candidates);
            }
        }
    }

    private static string? Property(XElement element, string name) =>
        element.Elements().FirstOrDefault(x => x.Name.LocalName.Equals("properties", StringComparison.OrdinalIgnoreCase))?
            .Elements().FirstOrDefault(x => x.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value.Trim();

    private static bool IsSecretKey(string key) =>
        key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("token", StringComparison.OrdinalIgnoreCase);

    private static bool HasExpectedRdpValueType(string key, string valueType) =>
        RdpStringKeys.Contains(key) ? valueType.Equals("s", StringComparison.OrdinalIgnoreCase) :
        RdpIntegerKeys.Contains(key) && valueType.Equals("i", StringComparison.OrdinalIgnoreCase);

    private static bool ReadBoolean(Dictionary<string, string> values, string key, bool fallback) =>
        values.TryGetValue(key, out var value) ? value is "1" or "*" : fallback;

    private static int ReadInt(Dictionary<string, string> values, string key, int fallback) =>
        values.TryGetValue(key, out var value) && int.TryParse(value, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static bool ReadDriveRedirection(Dictionary<string, string> values)
    {
        if (values.TryGetValue("drivestoredirect", out var drives)) return !string.IsNullOrWhiteSpace(drives);
        return ReadBoolean(values, "redirectdrives", false);
    }

    private static string? BuildUsernameHint(string? username, string? domain)
    {
        username = string.IsNullOrWhiteSpace(username) ? null : username.Trim();
        domain = string.IsNullOrWhiteSpace(domain) ? null : domain.Trim();
        if (username is null) return null;
        return domain is not null && !username.Contains('\\') && !username.Contains('@')
            ? $"{domain}\\{username}"
            : username;
    }

    private static (string Host, int? Port) SplitHostAndPort(string address)
    {
        address = address.Trim();
        if (address.StartsWith('[') && address.Contains(']'))
        {
            var end = address.IndexOf(']');
            var host = address[1..end];
            if (!IPAddress.TryParse(host, out var parsedAddress) || parsedAddress.AddressFamily != AddressFamily.InterNetworkV6)
                return (address, null);
            var suffix = address[(end + 1)..];
            if (suffix.Length == 0) return (host, null);
            if (suffix.Length > 1 && suffix[0] == ':' &&
                int.TryParse(suffix[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                return (host, parsed);
            return (address, null);
        }
        var colon = address.LastIndexOf(':');
        if (colon > 0 && address.Count(x => x == ':') == 1 &&
            int.TryParse(address[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            return (address[..colon], value);
        return (address, null);
    }

    private static List<string> ValidateCandidate(
        string name,
        string host,
        int port,
        string? username,
        IReadOnlyList<string> folderPath,
        RdpSettings settings)
    {
        var profile = new ConnectionProfile
        {
            Name = name,
            Host = host,
            Port = port,
            UsernameHint = username,
            Settings = settings
        };
        var errors = profile.Validate().ToList();
        if (folderPath.Count > ConnectionFolder.MaximumDepth) errors.Add($"Folder hierarchy cannot exceed {ConnectionFolder.MaximumDepth} levels.");
        foreach (var segment in folderPath)
        {
            var folder = new ConnectionFolder { Name = segment };
            errors.AddRange(folder.Validate().Select(error => $"Imported folder: {error}"));
        }
        return errors;
    }

    private static int GetMaximumElementDepth(XElement root)
    {
        var maximum = 0;
        var stack = new Stack<(XElement Element, int Depth)>();
        stack.Push((root, 1));
        while (stack.TryPop(out var current))
        {
            maximum = Math.Max(maximum, current.Depth);
            if (maximum > MaxRdgElementDepth) return maximum;
            foreach (var child in current.Element.Elements()) stack.Push((child, current.Depth + 1));
        }
        return maximum;
    }
}
