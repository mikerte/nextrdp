namespace NextRdp.Platform.Domain;

public sealed class PlatformAuditEvent
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid? WorkspaceId { get; set; }
    public Guid? ActorUserId { get; set; }
    public Guid? ServiceAccountId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public Guid? ResourceId { get; set; }
    public AuditOutcome Outcome { get; set; }
    public string RedactedDetailJson { get; set; } = "{}";
    public string CorrelationId { get; set; } = Guid.CreateVersion7().ToString("N");
    public DateTimeOffset OccurredAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SyncChange
{
    public long Sequence { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid? AudienceUserId { get; set; }
    public string ResourceType { get; set; } = string.Empty;
    public Guid ResourceId { get; set; }
    public SyncChangeKind Kind { get; set; }
    public long ResourceRevision { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset OccurredAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class OutboxMessage
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Type { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset OccurredAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ProcessedAtUtc { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptAtUtc { get; set; }
    public string LastErrorCode { get; set; } = string.Empty;
}

public sealed class IdempotencyRecord
{
    public Guid OrganizationId { get; set; }
    public string ActorKey { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public string ResponseJson { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAtUtc { get; set; }
}

public sealed class ServiceAccount
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid? WorkspaceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ScopesJson { get; set; } = "[]";
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>(PlatformValidation.ValidateName(Name, "Service account name"));
        if (OrganizationId == Guid.Empty || CreatedByUserId == Guid.Empty) errors.Add("Service account organization and creator are required.");
        if (string.IsNullOrWhiteSpace(ClientId) || ClientId.Length > 200 || ClientId.Any(char.IsControl)) errors.Add("Service account client ID is invalid.");
        if (ExpiresAtUtc is not null && ExpiresAtUtc <= CreatedAtUtc) errors.Add("Service account expiry must be after creation.");
        return errors;
    }
}

public sealed class ManagedImportPreviewRecord
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ActorId { get; set; }
    public string CandidatesJson { get; set; } = "[]";
    public int SecretFieldsDiscarded { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? CommittedAtUtc { get; set; }
}

public sealed class ProductSession
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public long SessionVersion { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset LastActivityAtUtc { get; set; }
    public DateTimeOffset IdleExpiresAtUtc { get; set; }
    public DateTimeOffset AbsoluteExpiresAtUtc { get; set; }
    public int IdleTimeoutMinutes { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public string ClientId { get; set; } = string.Empty;

    public bool IsActive(DateTimeOffset now, long currentSessionVersion) =>
        RevokedAtUtc is null &&
        SessionVersion == currentSessionVersion &&
        IdleExpiresAtUtc > now &&
        AbsoluteExpiresAtUtc > now;

    public void RecordActivity(DateTimeOffset now)
    {
        LastActivityAtUtc = now;
        IdleExpiresAtUtc = new DateTimeOffset(
            Math.Min(now.AddMinutes(IdleTimeoutMinutes).Ticks, AbsoluteExpiresAtUtc.Ticks),
            TimeSpan.Zero);
    }
}
