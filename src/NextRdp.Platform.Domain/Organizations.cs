namespace NextRdp.Platform.Domain;

public sealed class Organization
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>(PlatformValidation.ValidateName(Name, "Organization name"));
        if (!PlatformValidation.IsValidSlug(Slug)) errors.Add("Organization slug must contain 1-63 lowercase letters, digits, or interior hyphens.");
        return errors;
    }
}

public sealed class Workspace
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>(PlatformValidation.ValidateName(Name, "Workspace name"));
        if (OrganizationId == Guid.Empty) errors.Add("Workspace organization is required.");
        if (!PlatformValidation.IsValidSlug(Slug)) errors.Add("Workspace slug must contain 1-63 lowercase letters, digits, or interior hyphens.");
        if (Description.Length > 2000 || Description.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t'))
            errors.Add("Workspace description cannot exceed 2,000 characters or contain unsupported control characters.");
        return errors;
    }
}

public sealed class PlatformUser
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Issuer { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string EmailHint { get; set; } = string.Empty;
    public MembershipStatus Status { get; set; } = MembershipStatus.Active;
    public DateTimeOffset LastAuthenticatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public long SessionVersion { get; set; } = 1;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!Uri.TryCreate(Issuer, UriKind.Absolute, out var issuer) || issuer.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(issuer.UserInfo))
            errors.Add("User issuer must be an absolute HTTPS URI without user information.");
        if (string.IsNullOrWhiteSpace(Subject) || Subject.Length > 255 || Subject.Any(char.IsControl)) errors.Add("User subject is required and cannot exceed 255 characters.");
        errors.AddRange(PlatformValidation.ValidateName(DisplayName, "Display name", 200));
        if (EmailHint.Length > 320 || EmailHint.Any(char.IsControl)) errors.Add("Email hint cannot exceed 320 characters or contain control characters.");
        return errors;
    }
}

public sealed class WorkspaceMembership
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid? WorkspaceId { get; set; }
    public Guid UserId { get; set; }
    public WorkspaceRole Role { get; set; }
    public MembershipStatus Status { get; set; } = MembershipStatus.Active;
    public string SourceGroup { get; set; } = string.Empty;
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (OrganizationId == Guid.Empty) errors.Add("Membership organization is required.");
        if (UserId == Guid.Empty) errors.Add("Membership user is required.");
        if (SourceGroup.Length > 512 || SourceGroup.Any(char.IsControl)) errors.Add("Source group cannot exceed 512 characters or contain control characters.");
        if (Role is WorkspaceRole.WorkspaceAdministrator or WorkspaceRole.Operator && WorkspaceId is null)
            errors.Add("Workspace administrators and operators require a workspace scope.");
        return errors;
    }
}

public sealed class GroupRoleMapping
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid? WorkspaceId { get; set; }
    public string GroupClaim { get; set; } = string.Empty;
    public WorkspaceRole Role { get; set; }
    public bool Enabled { get; set; } = true;
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (OrganizationId == Guid.Empty) errors.Add("Group-role mapping organization is required.");
        if (string.IsNullOrWhiteSpace(GroupClaim) || GroupClaim.Length > 512 || GroupClaim.Any(char.IsControl))
            errors.Add("Group claim is required and cannot exceed 512 characters or contain control characters.");
        if (Role is WorkspaceRole.WorkspaceAdministrator or WorkspaceRole.Operator && WorkspaceId is null)
            errors.Add("Workspace administrators and operators require a workspace-scoped group mapping.");
        return errors;
    }
}
