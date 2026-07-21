namespace NextRdp.Platform.Domain;

public sealed class DynamicConnectionGroup
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string RulesJson { get; set; } = "[]";
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>(PlatformValidation.ValidateName(Name, "Dynamic group name"));
        if (OrganizationId == Guid.Empty || WorkspaceId == Guid.Empty) errors.Add("Dynamic group organization and workspace are required.");
        if (RulesJson.Length > 16_384) errors.Add("Dynamic group rules cannot exceed 16 KiB.");
        return errors;
    }
}

public sealed class ManagedConnectionTemplate
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string TemplateJson { get; set; } = "{}";
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>(PlatformValidation.ValidateName(Name, "Template name"));
        if (OrganizationId == Guid.Empty || WorkspaceId == Guid.Empty) errors.Add("Template organization and workspace are required.");
        if (Description.Length > 1000 || Description.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t'))
            errors.Add("Template description cannot exceed 1,000 characters or contain unsupported control characters.");
        if (TemplateJson.Length > 65_536) errors.Add("Template content cannot exceed 64 KiB.");
        return errors;
    }
}

public sealed class UserWorkspaceState
{
    public Guid OrganizationId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid UserId { get; set; }
    public string PinnedFolderIdsJson { get; set; } = "[]";
    public string LayoutJson { get; set; } = "{}";
    public string SavedWorkspacesJson { get; set; } = "[]";
    public string TabGroupsJson { get; set; } = "[]";
    public long Revision { get; set; } = 1;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
