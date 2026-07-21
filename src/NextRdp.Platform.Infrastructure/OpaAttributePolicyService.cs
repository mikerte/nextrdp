using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using NextRdp.Platform.Application;

namespace NextRdp.Platform.Infrastructure;

public sealed record OpaOptions(Uri BaseAddress, string DecisionPath = "v1/data/nextrdp/phase2/decision")
{
    public void Validate()
    {
        if (!BaseAddress.IsAbsoluteUri || BaseAddress.Scheme is not ("http" or "https")) throw new InvalidOperationException("OPA base address must be an absolute HTTP(S) URI.");
        if (string.IsNullOrWhiteSpace(DecisionPath) || DecisionPath.Length > 256 || DecisionPath.StartsWith('/') || DecisionPath.Any(char.IsControl))
            throw new InvalidOperationException("OPA decision path is invalid.");
    }
}

public sealed class OpaAttributePolicyService(HttpClient httpClient, OpaOptions options) : IAttributePolicyService
{
    public async Task<AuthorizationDecision> EvaluateAsync(
        PlatformActor actor,
        PlatformAction action,
        AuthorizationTarget target,
        IReadOnlyDictionary<string, object?> attributes,
        CancellationToken cancellationToken = default)
    {
        if (!actor.IsAuthenticated) return AuthorizationDecision.Deny("Authentication is required.");
        options.Validate();
        var request = new OpaRequest(new OpaInput(
            new OpaActor(actor.PrincipalId, actor.IsServiceAccount, actor.OrganizationId, actor.Roles.Select(role => role.ToString()).ToArray(), actor.Groups, actor.MfaSatisfied, actor.SessionVersion),
            action.ToString(),
            target,
            attributes));
        try
        {
            using var response = await httpClient.PostAsJsonAsync(options.DecisionPath, request, cancellationToken);
            if (!response.IsSuccessStatusCode) return AuthorizationDecision.Deny("The attribute policy service did not return a valid decision.");
            var body = await response.Content.ReadFromJsonAsync<OpaResponse>(cancellationToken: cancellationToken);
            return body?.Result?.Allow == true
                ? AuthorizationDecision.Allow("Allowed by central attribute policy.", body.Result.Rules ?? [])
                : AuthorizationDecision.Deny(body?.Result?.Reason ?? "Central attribute policy denied this action.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or NotSupportedException or JsonException)
        {
            return AuthorizationDecision.Deny("The attribute policy service is unavailable or returned an invalid decision.");
        }
    }

    private sealed record OpaRequest([property: JsonPropertyName("input")] OpaInput Input);
    private sealed record OpaInput(OpaActor Actor, string Action, AuthorizationTarget Target, IReadOnlyDictionary<string, object?> Attributes);
    private sealed record OpaActor(Guid PrincipalId, bool IsServiceAccount, Guid? OrganizationId, IReadOnlyList<string> Roles, IReadOnlyList<string> Groups, bool MfaSatisfied, long SessionVersion);
    private sealed record OpaResponse([property: JsonPropertyName("result")] OpaResult? Result);
    private sealed record OpaResult([property: JsonPropertyName("allow")] bool Allow, [property: JsonPropertyName("reason")] string? Reason, [property: JsonPropertyName("rules")] string[]? Rules);
}
