using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace NextRdp.Platform.Client;

public sealed class SystemBrowserOidcBroker(HttpClient httpClient) : IInteractiveTokenBroker
{
    private static readonly string[] Scopes = ["openid", "profile", "offline_access", "inventory.read", "configuration.read"];

    public async Task<DesktopTokenSet> SignInAsync(ManagedClientOptions options, CancellationToken cancellationToken = default)
    {
        options.Validate();
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var nonce = Base64Url(RandomNumberGenerator.GetBytes(32));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var authorize = new Uri(options.ControlPlane, "connect/authorize");
        var query = new Dictionary<string, string>
        {
            ["client_id"] = options.ClientId,
            ["redirect_uri"] = options.RedirectUri.AbsoluteUri,
            ["response_type"] = "code",
            ["scope"] = string.Join(' ', Scopes),
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256"
        };
        var authorizeUri = new UriBuilder(authorize) { Query = Form(query) }.Uri;
        var prefix = $"{options.RedirectUri.Scheme}://{options.RedirectUri.Host}:{options.RedirectUri.Port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        try { listener.Start(); }
        catch (HttpListenerException exception) { throw new InvalidOperationException($"The secure sign-in callback could not listen on {prefix}. Close the other program using this port and try again.", exception); }

        Process.Start(new ProcessStartInfo(authorizeUri.AbsoluteUri) { UseShellExecute = true });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        HttpListenerContext callback;
        try { callback = await listener.GetContextAsync().WaitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new TimeoutException("Corporate sign-in did not complete within three minutes."); }
        var parameters = ParseQuery(callback.Request.Url?.Query ?? string.Empty);
        var responseText = parameters.ContainsKey("error") ? "NextRDP sign-in failed. Return to the application for details." : "NextRDP sign-in completed. You can close this browser tab.";
        var bytes = Encoding.UTF8.GetBytes($"<!doctype html><html><head><meta charset=\"utf-8\"><title>NextRDP sign-in</title></head><body><h1>{WebUtility.HtmlEncode(responseText)}</h1></body></html>");
        callback.Response.StatusCode = 200;
        callback.Response.ContentType = "text/html; charset=utf-8";
        callback.Response.ContentLength64 = bytes.Length;
        await callback.Response.OutputStream.WriteAsync(bytes, cancellationToken);
        callback.Response.Close();
        if (!parameters.TryGetValue("state", out var returnedState) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(returnedState)))
            throw new InvalidOperationException("The corporate sign-in response did not match this application request.");
        if (parameters.TryGetValue("error", out var error)) throw new InvalidOperationException($"Corporate sign-in was rejected: {SafeOAuthValue(error)}.");
        if (!parameters.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code)) throw new InvalidOperationException("Corporate sign-in returned no authorization code.");
        return await ExchangeAsync(options, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = options.ClientId,
            ["code"] = code,
            ["redirect_uri"] = options.RedirectUri.AbsoluteUri,
            ["code_verifier"] = verifier
        }, cancellationToken);
    }

    public Task<DesktopTokenSet> RefreshAsync(ManagedClientOptions options, string refreshToken, CancellationToken cancellationToken = default) =>
        ExchangeAsync(options, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = options.ClientId,
            ["refresh_token"] = refreshToken,
            ["scope"] = string.Join(' ', Scopes)
        }, cancellationToken);

    private async Task<DesktopTokenSet> ExchangeAsync(ManagedClientOptions options, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(new Uri(options.ControlPlane, "connect/token"), new FormUrlEncodedContent(form), cancellationToken);
        var payload = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: cancellationToken);
        if (!response.IsSuccessStatusCode || payload is null || string.IsNullOrWhiteSpace(payload.AccessToken))
            throw new InvalidOperationException($"The control plane rejected the product token exchange ({(int)response.StatusCode}): {SafeOAuthValue(payload?.Error ?? "token_exchange_failed")}.");
        return new DesktopTokenSet(payload.AccessToken, payload.RefreshToken ?? string.Empty, DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, payload.ExpiresIn)), payload.TokenType ?? "Bearer", payload.Scope ?? string.Empty);
    }

    private static string Form(IReadOnlyDictionary<string, string> values) => string.Join('&', values.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
    private static string Base64Url(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static Dictionary<string, string> ParseQuery(string query) => query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(value => value.Split('=', 2)).ToDictionary(parts => Uri.UnescapeDataString(parts[0]), parts => parts.Length == 2 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : string.Empty, StringComparer.Ordinal);
    private static string SafeOAuthValue(string value) => value.Length > 100 || value.Any(char.IsControl) ? "invalid_response" : value;

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("token_type")] string? TokenType,
        [property: JsonPropertyName("scope")] string? Scope,
        [property: JsonPropertyName("error")] string? Error);
}
