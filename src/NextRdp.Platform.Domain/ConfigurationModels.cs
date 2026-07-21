namespace NextRdp.Platform.Domain;

public sealed class ConfigurationDraft
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid? WorkspaceId { get; set; }
    public ConfigurationScopeType ScopeType { get; set; }
    public Guid ScopeId { get; set; }
    public int BaseVersion { get; set; }
    public string ValuesJson { get; set; } = "{}";
    public string LockedFieldsJson { get; set; } = "[]";
    public ConfigurationLifecycle Lifecycle { get; set; } = ConfigurationLifecycle.Draft;
    public Guid CreatedByUserId { get; set; }
    public string Summary { get; set; } = string.Empty;
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (OrganizationId == Guid.Empty || ScopeId == Guid.Empty || CreatedByUserId == Guid.Empty) errors.Add("Draft organization, scope, and creator are required.");
        if (ScopeType != ConfigurationScopeType.Organization && WorkspaceId is null) errors.Add("Workspace, folder, and connection configuration require a workspace.");
        if (Summary.Length > 500 || Summary.Any(char.IsControl)) errors.Add("Configuration summary cannot exceed 500 characters or contain control characters.");
        if (ValuesJson.Length > 262144 || LockedFieldsJson.Length > 32768) errors.Add("Configuration draft exceeds the supported size.");
        return errors;
    }
}

public sealed class PublishedConfiguration
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid? WorkspaceId { get; set; }
    public ConfigurationScopeType ScopeType { get; set; }
    public Guid ScopeId { get; set; }
    public int Version { get; set; }
    public Guid SourceDraftId { get; set; }
    public Guid? RolledBackFromId { get; set; }
    public string ValuesJson { get; set; } = "{}";
    public string LockedFieldsJson { get; set; } = "[]";
    public Guid PublishedByUserId { get; set; }
    public string Summary { get; set; } = string.Empty;
    public DateTimeOffset PublishedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record EffectivePolicyField(string Name, string JsonValue, ConfigurationScopeType Source, Guid SourceId, bool Locked);

public sealed record EffectivePolicy(
    Guid OrganizationId,
    Guid WorkspaceId,
    Guid? FolderId,
    Guid? ConnectionId,
    long Revision,
    IReadOnlyDictionary<string, EffectivePolicyField> Fields,
    IReadOnlyList<string> Conflicts);
