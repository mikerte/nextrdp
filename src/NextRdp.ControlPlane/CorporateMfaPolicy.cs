using System.Security.Claims;
using NextRdp.Platform.Application;

namespace NextRdp.ControlPlane;

public sealed class CorporateMfaPolicy
{
    private CorporateMfaPolicy(bool required, string claimType, IReadOnlySet<string> acceptedValues)
    {
        Required = required;
        ClaimType = claimType;
        AcceptedValues = acceptedValues;
    }

    public bool Required { get; }
    public string ClaimType { get; }
    public IReadOnlySet<string> AcceptedValues { get; }

    public static CorporateMfaPolicy From(IConfiguration configuration, IHostEnvironment environment)
    {
        var section = configuration.GetSection("Authentication:CorporateOidc");
        var required = section.GetValue("RequireMfaClaim", true);
        if (!required && !environment.IsDevelopment())
            throw new InvalidOperationException("Corporate MFA claim enforcement cannot be disabled outside the Development environment.");

        var claimType = (section["MfaClaimType"] ?? "amr").Trim();
        var values = section.GetSection("MfaClaimValues").GetChildren()
            .Select(child => child.Value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (values.Length == 0) values = ["mfa"];
        if (claimType.Length is < 1 or > 256 || values.Length > 16 || values.Any(value => value.Length > 256))
            throw new InvalidOperationException("Corporate MFA claim configuration is invalid or exceeds supported bounds.");
        return new CorporateMfaPolicy(required, claimType, values.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    public bool Evaluate(ClaimsPrincipal principal, bool developmentIdentity)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (developmentIdentity) return true;
        var satisfied = principal.Claims.Any(claim => claim.Type.Equals(ClaimType, StringComparison.Ordinal) && AcceptedValues.Contains(claim.Value));
        if (Required && !satisfied)
            throw PlatformProblemException.Forbidden("The corporate identity provider must confirm MFA before sign-in.");
        return satisfied;
    }
}
