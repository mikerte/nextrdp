using NextRdp.Core;

namespace NextRdp.Platform.Domain;

public sealed class ManagedFolder
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid? ParentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>(PlatformValidation.ValidateName(Name, "Folder name"));
        if (OrganizationId == Guid.Empty || WorkspaceId == Guid.Empty) errors.Add("Folder organization and workspace are required.");
        if (ParentId == Id) errors.Add("A folder cannot be its own parent.");
        if (Name.IndexOfAny(['/', '\\']) >= 0) errors.Add("Folder name cannot contain path separators.");
        return errors;
    }
}

public sealed class ManagedConnection
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid? FolderId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public ResourceVisibility Visibility { get; set; } = ResourceVisibility.Shared;
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 3389;
    public string UsernameHint { get; set; } = string.Empty;
    public string ProvisioningKey { get; set; } = string.Empty;
    public ConnectionEnvironment Environment { get; set; } = ConnectionEnvironment.Development;
    public string Notes { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = [];
    public ManagedConnectionMetadata Metadata { get; set; } = new();
    public ManagedRdpSettings Settings { get; set; } = new();
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>(PlatformValidation.ValidateName(Name, "Connection name"));
        if (OrganizationId == Guid.Empty || WorkspaceId == Guid.Empty) errors.Add("Connection organization and workspace are required.");
        if (Visibility == ResourceVisibility.Personal && OwnerUserId is null) errors.Add("Personal connections require an owner.");
        if (!PlatformValidation.IsValidHost(Host)) errors.Add("Host must be a valid hostname or IP address without an embedded port.");
        if (Port is < 1 or > 65535) errors.Add("Port must be between 1 and 65535.");
        if (UsernameHint.Length > 320 || UsernameHint.Any(char.IsControl)) errors.Add("Username hint cannot exceed 320 characters or contain control characters.");
        if (ProvisioningKey.Length > 200 || ProvisioningKey.Any(char.IsControl)) errors.Add("Provisioning key cannot exceed 200 characters or contain control characters.");
        if (Notes.Length > 4000 || Notes.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t'))
            errors.Add("Notes cannot exceed 4,000 characters or contain unsupported control characters.");
        if (Tags.Count > 50 || Tags.Any(tag => string.IsNullOrWhiteSpace(tag) || tag.Length > 64 || tag.Any(char.IsControl)))
            errors.Add("Use at most 50 non-empty tags of 64 characters or fewer without control characters.");
        errors.AddRange(Metadata.Validate());
        errors.AddRange(Settings.Validate());
        return errors;
    }
}

public sealed class ManagedConnectionMetadata
{
    public string BusinessUnit { get; set; } = string.Empty;
    public string ApplicationOwner { get; set; } = string.Empty;
    public string ServiceOwner { get; set; } = string.Empty;
    public string Criticality { get; set; } = string.Empty;
    public string Geography { get; set; } = string.Empty;
    public string DataClassification { get; set; } = string.Empty;
    public string MaintenanceWindow { get; set; } = string.Empty;
    public string SupportGroup { get; set; } = string.Empty;
    public string EscalationContact { get; set; } = string.Empty;
    public string AssetId { get; set; } = string.Empty;
    public string CmdbId { get; set; } = string.Empty;
    public string OperatingSystem { get; set; } = string.Empty;
    public string MonitoringUrl { get; set; } = string.Empty;
    public string DocumentationUrl { get; set; } = string.Empty;
    public string TicketingUrl { get; set; } = string.Empty;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        foreach (var (label, value) in TextValues())
            if (value.Length > 512 || value.Any(char.IsControl)) errors.Add($"{label} cannot exceed 512 characters or contain control characters.");
        foreach (var (label, value) in LinkValues())
            if (!PlatformValidation.IsSafeHttpsLink(value)) errors.Add($"{label} must be an absolute HTTPS URL without embedded credentials.");
        return errors;
    }

    private IEnumerable<(string Label, string Value)> TextValues()
    {
        yield return ("Business unit", BusinessUnit);
        yield return ("Application owner", ApplicationOwner);
        yield return ("Service owner", ServiceOwner);
        yield return ("Criticality", Criticality);
        yield return ("Geography", Geography);
        yield return ("Data classification", DataClassification);
        yield return ("Maintenance window", MaintenanceWindow);
        yield return ("Support group", SupportGroup);
        yield return ("Escalation contact", EscalationContact);
        yield return ("Asset ID", AssetId);
        yield return ("CMDB ID", CmdbId);
        yield return ("Operating system", OperatingSystem);
    }

    private IEnumerable<(string Label, string Value)> LinkValues()
    {
        yield return ("Monitoring URL", MonitoringUrl);
        yield return ("Documentation URL", DocumentationUrl);
        yield return ("Ticketing URL", TicketingUrl);
    }
}
