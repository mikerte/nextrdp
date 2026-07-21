using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NextRdp.Platform.Application;
using NextRdp.Platform.Domain;

namespace NextRdp.Platform.Infrastructure;

public sealed partial class PlatformManagementService
{
    public async Task<SyncResponse> SynchronizeAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, SyncRequest request, CancellationToken cancellationToken = default)
    {
        await DemandAsync(actor, PlatformAction.ReadInventory, organizationId, workspaceId, "workspace", workspaceId,
            new Dictionary<string, object?> { ["clientVersion"] = request.ClientVersion }, cancellationToken);
        if (!SemanticVersion.TryParse(request.ClientVersion, out var clientVersion))
            throw PlatformProblemException.Validation(["Client version must be a valid semantic version."]);
        if (request.KnownRevisions.Count > 10000) throw PlatformProblemException.Validation(["Known revision map cannot exceed 10,000 resources."]);

        var sequence = cursorCodec.Decode(request.Cursor);
        var latestSequence = await dbContext.SyncChanges
            .Where(change => change.OrganizationId == organizationId && change.WorkspaceId == workspaceId)
            .Select(change => (long?)change.Sequence)
            .MaxAsync(cancellationToken) ?? 0;
        if (sequence > latestSequence) throw PlatformProblemException.Validation(["Synchronization cursor is newer than the server change feed."]);

        var changes = await dbContext.SyncChanges.AsNoTracking()
            .Where(change => change.OrganizationId == organizationId && change.WorkspaceId == workspaceId && change.Sequence > sequence &&
                (change.AudienceUserId == null || change.AudienceUserId == actor.PrincipalId))
            .OrderBy(change => change.Sequence)
            .Take(501)
            .ToListAsync(cancellationToken);
        var hasMore = changes.Count > 500;
        if (hasMore) changes.RemoveAt(changes.Count - 1);

        var items = new List<SyncItem>(changes.Count);
        foreach (var change in changes)
        {
            if (request.KnownRevisions.TryGetValue(change.ResourceId, out var knownRevision) && knownRevision >= change.ResourceRevision && change.Kind == SyncChangeKind.Upsert)
                continue;
            JsonElement? payload = null;
            if (change.Kind == SyncChangeKind.Upsert)
            {
                using var document = JsonDocument.Parse(change.PayloadJson);
                payload = document.RootElement.Clone();
            }
            items.Add(new SyncItem(change.ResourceType, change.ResourceId, change.Kind, change.ResourceRevision, payload));
        }

        var effective = await GetEffectivePolicyAsync(actor, organizationId, workspaceId, null, null, cancellationToken);
        var minimumVersion = "0.1.0";
        if (effective.Fields.TryGetValue("client.minimumVersion", out var minimumField))
            minimumVersion = JsonSerializer.Deserialize<string>(minimumField.JsonValue, JsonOptions) ?? minimumVersion;
        if (!SemanticVersion.TryParse(minimumVersion, out var minimum)) minimum = new SemanticVersion(0, 1, 0);
        var nextSequence = changes.Count == 0 ? sequence : changes[^1].Sequence;

        return new SyncResponse(
            items,
            cursorCodec.Encode(nextSequence),
            timeProvider.GetUtcNow(),
            minimum.ToString(),
            clientVersion < minimum,
            effective.Revision,
            hasMore);
    }

    public async Task<IReadOnlyList<AuditEventDto>> ListAuditAsync(PlatformActor actor, Guid organizationId, Guid? workspaceId, int limit, CancellationToken cancellationToken = default)
    {
        await DemandAsync(actor, PlatformAction.ReadAudit, organizationId, workspaceId, workspaceId is null ? "organization" : "workspace", workspaceId ?? organizationId, cancellationToken: cancellationToken);
        if (limit is < 1 or > 1000) throw PlatformProblemException.Validation(["Audit limit must be between 1 and 1,000."]);
        var events = await dbContext.AuditEvents.AsNoTracking()
            .Where(entity => entity.OrganizationId == organizationId && (workspaceId == null || entity.WorkspaceId == workspaceId))
            .OrderByDescending(entity => entity.OccurredAtUtc)
            .ThenByDescending(entity => entity.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
        return events.Select(ToAuditDto).ToArray();
    }

    private static AuditEventDto ToAuditDto(PlatformAuditEvent entity)
    {
        using var detail = JsonDocument.Parse(entity.RedactedDetailJson);
        var alias = entity.ActorUserId is not null ? $"user:{entity.ActorUserId.Value:N}"[..13] :
            entity.ServiceAccountId is not null ? $"service:{entity.ServiceAccountId.Value:N}"[..16] : "system";
        return new AuditEventDto(entity.Id, entity.OrganizationId, entity.WorkspaceId, alias, entity.Action, entity.ResourceType, entity.ResourceId,
            entity.Outcome, detail.RootElement.Clone(), entity.CorrelationId, entity.OccurredAtUtc);
    }
}
