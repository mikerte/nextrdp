using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using NextRdp.Platform.Client;

namespace NextRdp.Cli;

public static class Program
{
    public static Task<int> Main(string[] args) => CliApplication.RunAsync(args);
}

internal static class CliApplication
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<int> RunAsync(string[] arguments)
    {
        try
        {
            var parsed = CliArguments.Parse(arguments);
            if (parsed.Help || string.IsNullOrWhiteSpace(parsed.Command)) { Usage(); return parsed.Help ? 0 : 2; }
            using var http = new HttpClient { BaseAddress = parsed.Endpoint, Timeout = TimeSpan.FromSeconds(30) };
            string token;
            if (parsed.Interactive)
            {
                var options = new ManagedClientOptions(parsed.Endpoint, "nextrdp-desktop", new Uri("http://127.0.0.1:43876/callback"), parsed.Value("client-version") ?? "0.2.0");
                var tokenSet = await new SystemBrowserOidcBroker(http).SignInAsync(options);
                token = tokenSet.AccessToken;
            }
            else
            {
                var secret = Environment.GetEnvironmentVariable("NEXTRDP_CLIENT_SECRET");
                if (string.IsNullOrWhiteSpace(parsed.ClientId) || string.IsNullOrWhiteSpace(secret))
                    throw new CliException(3, "authentication_required", "Use --interactive for an approved user sign-in, or set --client-id and the NEXTRDP_CLIENT_SECRET environment variable for a service account. Secrets are intentionally not accepted on the command line.");
                token = await GetTokenAsync(http, parsed.ClientId, secret, parsed.Scope);
            }
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return await ExecuteAsync(http, parsed);
        }
        catch (CliException exception)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { error = exception.Code, message = exception.Message }, JsonOptions));
            return exception.ExitCode;
        }
        catch (HttpRequestException exception)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { error = "network_error", message = exception.Message }, JsonOptions));
            return 7;
        }
        catch (TaskCanceledException)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { error = "request_timeout", message = "The control-plane request timed out." }, JsonOptions));
            return 7;
        }
        catch (Exception exception) when (exception is IOException or JsonException or ArgumentException)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { error = "local_input_error", message = exception.Message }, JsonOptions));
            return 2;
        }
    }

    private static async Task<int> ExecuteAsync(HttpClient http, CliArguments args)
    {
        Guid organization;
        string path;
        object? request = null;
        HttpMethod method = HttpMethod.Get;
        switch (args.Command)
        {
            case "organizations": path = "api/v1/organizations"; break;
            case "workspaces": organization = args.RequireGuid("organization"); path = $"api/v1/organizations/{organization:D}/workspaces"; break;
            case "connections":
                organization = args.RequireGuid("organization");
                var workspace = args.RequireGuid("workspace");
                var search = args.Value("search");
                path = $"api/v1/organizations/{organization:D}/workspaces/{workspace:D}/connections?pageSize=200" + (string.IsNullOrWhiteSpace(search) ? string.Empty : $"&search={Uri.EscapeDataString(search)}");
                break;
            case "export":
                organization = args.RequireGuid("organization");
                workspace = args.RequireGuid("workspace");
                path = $"api/v1/organizations/{organization:D}/workspaces/{workspace:D}/export";
                var export = await SendAsync(http, HttpMethod.Get, path, null, null);
                var output = args.RequireValue("output");
                await File.WriteAllTextAsync(output, export.GetRawText());
                Console.WriteLine(JsonSerializer.Serialize(new { status = "ok", output = Path.GetFullPath(output), secretFree = true }, JsonOptions));
                return 0;
            case "sync":
                organization = args.RequireGuid("organization");
                workspace = args.RequireGuid("workspace");
                method = HttpMethod.Post; path = $"api/v1/organizations/{organization:D}/workspaces/{workspace:D}/sync";
                request = new { cursor = (string?)null, clientVersion = args.Value("client-version") ?? "0.2.0", knownRevisions = new Dictionary<Guid, long>() };
                break;
            case "gateway-diagnostic":
                organization = args.RequireGuid("organization");
                workspace = args.RequireGuid("workspace");
                var host = args.RequireValue("host"); var port = args.IntValue("port", 443);
                method = HttpMethod.Post; path = $"api/v1/organizations/{organization:D}/workspaces/{workspace:D}/gateway/diagnostics";
                request = new { host, port, requireTls = !args.Flag("no-tls"), expectedServerName = host };
                break;
            case "import-preview":
                organization = args.RequireGuid("organization");
                workspace = args.RequireGuid("workspace");
                var file = args.RequireValue("file");
                method = HttpMethod.Post; path = $"api/v1/organizations/{organization:D}/workspaces/{workspace:D}/imports/preview";
                request = new { format = args.Value("format") ?? Path.GetExtension(file).TrimStart('.'), content = await File.ReadAllTextAsync(file) };
                break;
            case "bulk-provision":
                organization = args.RequireGuid("organization");
                workspace = args.RequireGuid("workspace");
                file = args.RequireValue("file");
                method = HttpMethod.Post; path = $"api/v1/organizations/{organization:D}/workspaces/{workspace:D}/bulk/connections";
                request = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(file));
                break;
            default: throw new CliException(2, "unknown_command", $"Unknown command '{args.Command}'.");
        }
        var idempotencyKey = args.Command == "bulk-provision" ? args.Value("idempotency-key") ?? Guid.CreateVersion7().ToString("N") : null;
        var result = await SendAsync(http, method, path, request, idempotencyKey);
        Console.WriteLine(result.GetRawText());
        return 0;
    }

    private static async Task<string> GetTokenAsync(HttpClient http, string clientId, string clientSecret, string scope)
    {
        using var response = await http.PostAsync("connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["scope"] = scope
        }));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (!response.IsSuccessStatusCode || !body.TryGetProperty("access_token", out var accessToken))
            throw new CliException(3, "authentication_failed", "The control plane rejected the service-account credentials or requested scope.");
        return accessToken.GetString() ?? throw new CliException(3, "authentication_failed", "The token response contained no access token.");
    }

    private static async Task<JsonElement> SendAsync(HttpClient http, HttpMethod method, string path, object? body, string? idempotencyKey)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
        if (!string.IsNullOrWhiteSpace(idempotencyKey)) request.Headers.Add("Idempotency-Key", idempotencyKey);
        using var response = await http.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            string code = "request_failed", message = $"Request failed ({(int)response.StatusCode}).";
            try { var problem = JsonSerializer.Deserialize<JsonElement>(content); if (problem.TryGetProperty("code", out var value)) code = value.GetString() ?? code; if (problem.TryGetProperty("detail", out value)) message = value.GetString() ?? message; }
            catch (JsonException) { }
            var exit = response.StatusCode switch { HttpStatusCode.Unauthorized => 3, HttpStatusCode.Forbidden => 4, HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => 5, HttpStatusCode.Conflict or HttpStatusCode.PreconditionRequired => 6, _ => 7 };
            throw new CliException(exit, code, message);
        }
        return string.IsNullOrWhiteSpace(content) ? JsonSerializer.SerializeToElement(new { status = "ok" }, JsonOptions) : JsonSerializer.Deserialize<JsonElement>(content);
    }

    private static void Usage() => Console.WriteLine("""
NextRDP CLI
Usage: nextrdp --endpoint https://control-plane.example [--interactive | --client-id CLIENT_ID] COMMAND [options]
Interactive mode uses browser-based OIDC with PKCE for approved users. Service-account secrets are read only from NEXTRDP_CLIENT_SECRET.
Commands: organizations, workspaces, connections, export, sync, gateway-diagnostic, import-preview, bulk-provision
Common resource options: --organization GUID --workspace GUID
Exit codes: 0 success, 2 usage/input, 3 authentication, 4 authorization, 5 validation, 6 conflict, 7 network/server.
""");
}

internal sealed class CliArguments
{
    private readonly Dictionary<string, string?> options = new(StringComparer.Ordinal);
    public string Command { get; private set; } = string.Empty;
    public Uri Endpoint { get; private set; } = new("http://127.0.0.1:5080/");
    public string ClientId => Value("client-id") ?? string.Empty;
    public string Scope => Value("scope") ?? "inventory.read";
    public bool Interactive => Flag("interactive");
    public bool Help => Flag("help") || Flag("h");

    public static CliArguments Parse(string[] args)
    {
        var result = new CliArguments();
        for (var index = 0; index < args.Length; index++)
        {
            var current = args[index];
            if (!current.StartsWith("--", StringComparison.Ordinal)) { if (string.IsNullOrEmpty(result.Command)) result.Command = current.ToLowerInvariant(); else throw new CliException(2, "unexpected_argument", $"Unexpected argument '{current}'."); continue; }
            var name = current[2..].ToLowerInvariant();
            if (name is "help" or "h" or "no-tls" or "interactive") { result.options[name] = null; continue; }
            if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal)) throw new CliException(2, "missing_option_value", $"Option --{name} requires a value.");
            result.options[name] = args[index];
        }
        var endpointText = result.Value("endpoint") ?? Environment.GetEnvironmentVariable("NEXTRDP_ENDPOINT") ?? "http://127.0.0.1:5080/";
        if (!Uri.TryCreate(endpointText.EndsWith('/') ? endpointText : endpointText + '/', UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("https" or "http") || endpoint.Scheme == "http" && !endpoint.IsLoopback)
            throw new CliException(2, "invalid_endpoint", "Endpoint must be absolute HTTPS, or HTTP on loopback only.");
        result.Endpoint = endpoint;
        return result;
    }

    public string? Value(string name) => options.GetValueOrDefault(name);
    public string RequireValue(string name) => Value(name) is { Length: > 0 } value ? value : throw new CliException(2, "missing_option", $"--{name} is required.");
    public Guid RequireGuid(string name) => Guid.TryParse(RequireValue(name), out var value) ? value : throw new CliException(2, "invalid_guid", $"--{name} must be a GUID.");
    public int IntValue(string name, int fallback) => Value(name) is null ? fallback : int.TryParse(Value(name), out var value) ? value : throw new CliException(2, "invalid_integer", $"--{name} must be an integer.");
    public bool Flag(string name) => options.ContainsKey(name);
}

internal sealed class CliException(int exitCode, string code, string message) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
    public string Code { get; } = code;
}
