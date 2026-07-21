using NextRdp.Platform.Application;
using NextRdp.Platform.Domain;

namespace NextRdp.ControlPlane;

public sealed record RollbackConfigurationRequest(string Summary);
public sealed record RestoreBackupRequest(string ContentBase64, string Passphrase);

public static class NextRdpApiEndpoints
{
    public static IEndpointRouteBuilder MapNextRdpApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1")
            .RequireAuthorization("NextRdpApi")
            .WithTags("NextRDP Phase 2");

        api.MapGet("/organizations", async (HttpContext context, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListOrganizationsAsync(await actors.GetAsync(context, cancellationToken), cancellationToken)));
        Protect(api.MapPost("/organizations", async (HttpContext context, CreateOrganizationRequest request, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
        {
            var created = await service.CreateOrganizationAsync(await actors.GetAsync(context, cancellationToken), request, cancellationToken);
            return Results.Created($"/api/v1/organizations/{created.Id:D}", created);
        }));

        api.MapGet("/organizations/{organizationId:guid}/workspaces", async (HttpContext context, Guid organizationId, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListWorkspacesAsync(await actors.GetAsync(context, cancellationToken), organizationId, cancellationToken)));
        Protect(api.MapPost("/organizations/{organizationId:guid}/workspaces", async (HttpContext context, Guid organizationId, CreateWorkspaceRequest request, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
        {
            var created = await service.CreateWorkspaceAsync(await actors.GetAsync(context, cancellationToken), organizationId, request, cancellationToken);
            return Results.Created($"/api/v1/organizations/{organizationId:D}/workspaces/{created.Id:D}", created);
        }));
        api.MapGet("/organizations/{organizationId:guid}/users", async (HttpContext context, Guid organizationId, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListUsersAsync(await actors.GetAsync(context, cancellationToken), organizationId, cancellationToken)));
        api.MapGet("/organizations/{organizationId:guid}/memberships", async (HttpContext context, Guid organizationId, Guid? workspaceId, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListMembershipsAsync(await actors.GetAsync(context, cancellationToken), organizationId, workspaceId, cancellationToken)));
        Protect(api.MapPost("/organizations/{organizationId:guid}/memberships", async (HttpContext context, Guid organizationId, UpsertMembershipRequest request, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
        {
            var created = await service.SaveMembershipAsync(await actors.GetAsync(context, cancellationToken), organizationId, null, request with { ExpectedRevision = null }, cancellationToken);
            return RevisionResult(context, created, created.Revision, StatusCodes.Status201Created, $"/api/v1/organizations/{organizationId:D}/memberships/{created.Id:D}");
        }));
        Protect(api.MapPut("/organizations/{organizationId:guid}/memberships/{membershipId:guid}", async (HttpContext context, Guid organizationId, Guid membershipId, UpsertMembershipRequest request, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
        {
            var saved = await service.SaveMembershipAsync(await actors.GetAsync(context, cancellationToken), organizationId, membershipId, request with { ExpectedRevision = RequiredRevision(context) }, cancellationToken);
            return RevisionResult(context, saved, saved.Revision);
        }));
        Protect(api.MapDelete("/organizations/{organizationId:guid}/memberships/{membershipId:guid}", async (HttpContext context, Guid organizationId, Guid membershipId, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
        {
            await service.RevokeMembershipAsync(await actors.GetAsync(context, cancellationToken), organizationId, membershipId, RequiredRevision(context), cancellationToken);
            return Results.NoContent();
        }));
        api.MapGet("/organizations/{organizationId:guid}/group-role-mappings", async (HttpContext context, Guid organizationId, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListGroupRoleMappingsAsync(await actors.GetAsync(context, cancellationToken), organizationId, cancellationToken)));
        Protect(api.MapPost("/organizations/{organizationId:guid}/group-role-mappings", async (HttpContext context, Guid organizationId, SaveGroupRoleMappingRequest request, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
        {
            var created = await service.SaveGroupRoleMappingAsync(await actors.GetAsync(context, cancellationToken), organizationId, null, request with { ExpectedRevision = null }, cancellationToken);
            return RevisionResult(context, created, created.Revision, StatusCodes.Status201Created, $"/api/v1/organizations/{organizationId:D}/group-role-mappings/{created.Id:D}");
        }));
        Protect(api.MapPut("/organizations/{organizationId:guid}/group-role-mappings/{mappingId:guid}", async (HttpContext context, Guid organizationId, Guid mappingId, SaveGroupRoleMappingRequest request, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
        {
            var saved = await service.SaveGroupRoleMappingAsync(await actors.GetAsync(context, cancellationToken), organizationId, mappingId, request with { ExpectedRevision = RequiredRevision(context) }, cancellationToken);
            return RevisionResult(context, saved, saved.Revision);
        }));
        Protect(api.MapDelete("/organizations/{organizationId:guid}/group-role-mappings/{mappingId:guid}", async (HttpContext context, Guid organizationId, Guid mappingId, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
        {
            await service.DeleteGroupRoleMappingAsync(await actors.GetAsync(context, cancellationToken), organizationId, mappingId, RequiredRevision(context), cancellationToken);
            return Results.NoContent();
        }));

        api.MapGet("/organizations/{organizationId:guid}/workspaces/{workspaceId:guid}/folders", async (HttpContext context, Guid organizationId, Guid workspaceId, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListFoldersAsync(await actors.GetAsync(context, cancellationToken), organizationId, workspaceId, cancellationToken)));
        Protect(api.MapPost("/organizations/{organizationId:guid}/workspaces/{workspaceId:guid}/folders", async (HttpContext context, Guid organizationId, Guid workspaceId, UpsertFolderRequest request, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
        {
            var created = await service.SaveFolderAsync(await actors.GetAsync(context, cancellationToken), organizationId, workspaceId, null, request with { ExpectedRevision = null }, cancellationToken);
            return RevisionResult(context, created, created.Revision, StatusCodes.Status201Created, $"/api/v1/organizations/{organizationId:D}/workspaces/{workspaceId:D}/folders/{created.Id:D}");
        }));
        Protect(api.MapPut("/organizations/{organizationId:guid}/workspaces/{workspaceId:guid}/folders/{folderId:guid}", async (HttpContext context, Guid organizationId, Guid workspaceId, Guid folderId, UpsertFolderRequest request, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
        {
            var saved = await service.SaveFolderAsync(await actors.GetAsync(context, cancellationToken), organizationId, workspaceId, folderId, request with { ExpectedRevision = RequiredRevision(context) }, cancellationToken);
            return RevisionResult(context, saved, saved.Revision);
        }));

        api.MapGet("/organizations/{organizationId:guid}/workspaces/{workspaceId:guid}/connections", async (HttpContext context, Guid organizationId, Guid workspaceId, string? search, string? cursor, int? pageSize, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListConnectionsAsync(await actors.GetAsync(context, cancellationToken), organizationId, workspaceId,
                string.IsNullOrWhiteSpace(search) ? null : search, cursor, Math.Clamp(pageSize ?? 100, 1, 200), cancellationToken)));
        api.MapGet("/organizations/{organizationId:guid}/workspaces/{workspaceId:guid}/connections/{connectionId:guid}", async (HttpContext context, Guid organizationId, Guid workspaceId, Guid connectionId, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
        {
            var result = await service.GetConnectionAsync(await actors.GetAsync(context, cancellationToken), organizationId, workspaceId, connectionId, cancellationToken);
            return RevisionResult(context, result, result.Revision);
        });
        Protect(api.MapPost("/organizations/{organizationId:guid}/workspaces/{workspaceId:guid}/connections", async (HttpContext context, Guid organizationId, Guid workspaceId, UpsertManagedConnectionRequest request, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
        {
            var created = await service.SaveConnectionAsync(await actors.GetAsync(context, cancellationToken), organizationId, workspaceId, null, request with { ExpectedRevision = null }, cancellationToken);
            return RevisionResult(context, created, created.Revision, StatusCodes.Status201Created, $"/api/v1/organizations/{organizationId:D}/workspaces/{workspaceId:D}/connections/{created.Id:D}");
        }));
        Protect(api.MapPut("/organizations/{organizationId:guid}/workspaces/{workspaceId:guid}/connections/{connectionId:guid}", async (HttpContext context, Guid organizationId, Guid workspaceId, Guid connectionId, UpsertManagedConnectionRequest request, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
        {
            var saved = await service.SaveConnectionAsync(await actors.GetAsync(context, cancellationToken), organizationId, workspaceId, connectionId, request with { ExpectedRevision = RequiredRevision(context) }, cancellationToken);
            return RevisionResult(context, saved, saved.Revision);
        }));
        Protect(api.MapDelete("/organizations/{organizationId:guid}/workspaces/{workspaceId:guid}/connections/{connectionId:guid}", async (HttpContext context, Guid organizationId, Guid workspaceId, Guid connectionId, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
        {
            await service.DeleteConnectionAsync(await actors.GetAsync(context, cancellationToken), organizationId, workspaceId, connectionId, RequiredRevision(context), cancellationToken);
            return Results.NoContent();
        }));

        Protect(api.MapPost("/organizations/{organizationId:guid}/configuration/drafts", async (HttpContext context, Guid organizationId, Guid? workspaceId, SaveConfigurationDraftRequest request, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
        {
            var created = await service.SaveConfigurationDraftAsync(await actors.GetAsync(context, cancellationToken), organizationId, workspaceId, null, request with { ExpectedRevision = null }, cancellationToken);
            return RevisionResult(context, created, created.Revision, StatusCodes.Status201Created, $"/api/v1/organizations/{organizationId:D}/configuration/drafts/{created.Id:D}");
        }));
        Protect(api.MapPut("/organizations/{organizationId:guid}/configuration/drafts/{draftId:guid}", async (HttpContext context, Guid organizationId, Guid? workspaceId, Guid draftId, SaveConfigurationDraftRequest request, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
        {
            var saved = await service.SaveConfigurationDraftAsync(await actors.GetAsync(context, cancellationToken), organizationId, workspaceId, draftId, request with { ExpectedRevision = RequiredRevision(context) }, cancellationToken);
            return RevisionResult(context, saved, saved.Revision);
        }));
        Protect(api.MapPost("/organizations/{organizationId:guid}/configuration/drafts/{draftId:guid}/publish", async (HttpContext context, Guid organizationId, Guid? workspaceId, Guid draftId, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.PublishConfigurationAsync(await actors.GetAsync(context, cancellationToken), organizationId, workspaceId, draftId, cancellationToken))));
        Protect(api.MapPost("/organizations/{organizationId:guid}/configuration/published/{publishedConfigurationId:guid}/rollback", async (HttpContext context, Guid organizationId, Guid? workspaceId, Guid publishedConfigurationId, RollbackConfigurationRequest request, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.RollbackConfigurationAsync(await actors.GetAsync(context, cancellationToken), organizationId, workspaceId, publishedConfigurationId, request.Summary, cancellationToken))));
        api.MapGet("/organizations/{organizationId:guid}/configuration/history", async (HttpContext context, Guid organizationId, Guid? workspaceId, ConfigurationScopeType scopeType, Guid scopeId, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListConfigurationHistoryAsync(await actors.GetAsync(context, cancellationToken), organizationId, workspaceId, scopeType, scopeId, cancellationToken)));
        api.MapGet("/organizations/{organizationId:guid}/workspaces/{workspaceId:guid}/configuration/effective", async (HttpContext context, Guid organizationId, Guid workspaceId, Guid? folderId, Guid? connectionId, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetEffectivePolicyAsync(await actors.GetAsync(context, cancellationToken), organizationId, workspaceId, folderId, connectionId, cancellationToken)));

        Protect(api.MapPost("/organizations/{organizationId:guid}/workspaces/{workspaceId:guid}/sync", async (HttpContext context, Guid organizationId, Guid workspaceId, SyncRequest request, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.SynchronizeAsync(await actors.GetAsync(context, cancellationToken), organizationId, workspaceId, request, cancellationToken))));
        api.MapGet("/organizations/{organizationId:guid}/audit", async (HttpContext context, Guid organizationId, Guid? workspaceId, int? limit, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAuditAsync(await actors.GetAsync(context, cancellationToken), organizationId, workspaceId, Math.Clamp(limit ?? 100, 1, 1000), cancellationToken)));

        Protect(api.MapPost("/organizations/{organizationId:guid}/workspaces/{workspaceId:guid}/bulk/connections", async (HttpContext context, Guid organizationId, Guid workspaceId, BulkProvisionRequest request, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.BulkProvisionAsync(await actors.GetAsync(context, cancellationToken), organizationId, workspaceId, RequiredIdempotencyKey(context), request, cancellationToken))));
        Protect(api.MapPost("/organizations/{organizationId:guid}/workspaces/{workspaceId:guid}/imports/preview", async (HttpContext context, Guid organizationId, Guid workspaceId, ImportPreviewRequest request, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.PreviewImportAsync(await actors.GetAsync(context, cancellationToken), organizationId, workspaceId, request, cancellationToken))));
        Protect(api.MapPost("/organizations/{organizationId:guid}/workspaces/{workspaceId:guid}/imports/commit", async (HttpContext context, Guid organizationId, Guid workspaceId, CommitImportRequest request, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.CommitImportAsync(await actors.GetAsync(context, cancellationToken), organizationId, workspaceId, request, cancellationToken))));
        api.MapGet("/organizations/{organizationId:guid}/workspaces/{workspaceId:guid}/export", async (HttpContext context, Guid organizationId, Guid workspaceId, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ExportInventoryAsync(await actors.GetAsync(context, cancellationToken), organizationId, workspaceId, cancellationToken)));

        api.MapGet("/organizations/{organizationId:guid}/workspaces/{workspaceId:guid}/dynamic-groups", async (HttpContext context, Guid organizationId, Guid workspaceId, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListDynamicGroupsAsync(await actors.GetAsync(context, cancellationToken), organizationId, workspaceId, cancellationToken)));
        Protect(api.MapPost("/organizations/{organizationId:guid}/workspaces/{workspaceId:guid}/dynamic-groups", async (HttpContext context, Guid organizationId, Guid workspaceId, SaveDynamicGroupRequest request, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
        {
            var created = await service.SaveDynamicGroupAsync(await actors.GetAsync(context, cancellationToken), organizationId, workspaceId, null, request with { ExpectedRevision = null }, cancellationToken);
            return RevisionResult(context, created, created.Revision, StatusCodes.Status201Created, $"/api/v1/organizations/{organizationId:D}/workspaces/{workspaceId:D}/dynamic-groups/{created.Id:D}");
        }));
        Protect(api.MapPut("/organizations/{organizationId:guid}/workspaces/{workspaceId:guid}/dynamic-groups/{groupId:guid}", async (HttpContext context, Guid organizationId, Guid workspaceId, Guid groupId, SaveDynamicGroupRequest request, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
        {
            var saved = await service.SaveDynamicGroupAsync(await actors.GetAsync(context, cancellationToken), organizationId, workspaceId, groupId, request with { ExpectedRevision = RequiredRevision(context) }, cancellationToken);
            return RevisionResult(context, saved, saved.Revision);
        }));
        Protect(api.MapDelete("/organizations/{organizationId:guid}/workspaces/{workspaceId:guid}/dynamic-groups/{groupId:guid}", async (HttpContext context, Guid organizationId, Guid workspaceId, Guid groupId, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
        {
            await service.DeleteDynamicGroupAsync(await actors.GetAsync(context, cancellationToken), organizationId, workspaceId, groupId, RequiredRevision(context), cancellationToken);
            return Results.NoContent();
        }));

        api.MapGet("/organizations/{organizationId:guid}/workspaces/{workspaceId:guid}/connection-templates", async (HttpContext context, Guid organizationId, Guid workspaceId, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListConnectionTemplatesAsync(await actors.GetAsync(context, cancellationToken), organizationId, workspaceId, cancellationToken)));
        Protect(api.MapPost("/organizations/{organizationId:guid}/workspaces/{workspaceId:guid}/connection-templates", async (HttpContext context, Guid organizationId, Guid workspaceId, SaveConnectionTemplateRequest request, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
        {
            var created = await service.SaveConnectionTemplateAsync(await actors.GetAsync(context, cancellationToken), organizationId, workspaceId, null, request with { ExpectedRevision = null }, cancellationToken);
            return RevisionResult(context, created, created.Revision, StatusCodes.Status201Created, $"/api/v1/organizations/{organizationId:D}/workspaces/{workspaceId:D}/connection-templates/{created.Id:D}");
        }));
        Protect(api.MapPut("/organizations/{organizationId:guid}/workspaces/{workspaceId:guid}/connection-templates/{templateId:guid}", async (HttpContext context, Guid organizationId, Guid workspaceId, Guid templateId, SaveConnectionTemplateRequest request, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
        {
            var saved = await service.SaveConnectionTemplateAsync(await actors.GetAsync(context, cancellationToken), organizationId, workspaceId, templateId, request with { ExpectedRevision = RequiredRevision(context) }, cancellationToken);
            return RevisionResult(context, saved, saved.Revision);
        }));
        Protect(api.MapPost("/organizations/{organizationId:guid}/workspaces/{workspaceId:guid}/connections/from-template", async (HttpContext context, Guid organizationId, Guid workspaceId, CreateConnectionFromTemplateRequest request, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
        {
            var created = await service.CreateConnectionFromTemplateAsync(await actors.GetAsync(context, cancellationToken), organizationId, workspaceId, request, cancellationToken);
            return RevisionResult(context, created, created.Revision, StatusCodes.Status201Created, $"/api/v1/organizations/{organizationId:D}/workspaces/{workspaceId:D}/connections/{created.Id:D}");
        }));
        Protect(api.MapPost("/organizations/{organizationId:guid}/workspaces/{workspaceId:guid}/connections/{connectionId:guid}/copy", async (HttpContext context, Guid organizationId, Guid workspaceId, Guid connectionId, CopyManagedConnectionRequest request, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
        {
            var created = await service.CopyConnectionAsync(await actors.GetAsync(context, cancellationToken), organizationId, workspaceId, connectionId, request, cancellationToken);
            return RevisionResult(context, created, created.Revision, StatusCodes.Status201Created, $"/api/v1/organizations/{organizationId:D}/workspaces/{workspaceId:D}/connections/{created.Id:D}");
        }));

        api.MapGet("/organizations/{organizationId:guid}/workspaces/{workspaceId:guid}/me/workspace-state", async (HttpContext context, Guid organizationId, Guid workspaceId, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetUserWorkspaceStateAsync(await actors.GetAsync(context, cancellationToken), organizationId, workspaceId, cancellationToken)));
        Protect(api.MapPut("/organizations/{organizationId:guid}/workspaces/{workspaceId:guid}/me/workspace-state", async (HttpContext context, Guid organizationId, Guid workspaceId, SaveUserWorkspaceStateRequest request, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
        {
            var expected = OptionalRevision(context);
            var saved = await service.SaveUserWorkspaceStateAsync(await actors.GetAsync(context, cancellationToken), organizationId, workspaceId, request with { ExpectedRevision = expected }, cancellationToken);
            return RevisionResult(context, saved, saved.Revision);
        }));
        Protect(api.MapPost("/organizations/{organizationId:guid}/workspaces/{workspaceId:guid}/bulk/edit-connections", async (HttpContext context, Guid organizationId, Guid workspaceId, BulkConnectionEditRequest request, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.BulkEditConnectionsAsync(await actors.GetAsync(context, cancellationToken), organizationId, workspaceId, RequiredIdempotencyKey(context), request, cancellationToken))));
        Protect(api.MapPost("/organizations/{organizationId:guid}/workspaces/{workspaceId:guid}/bulk/connect-preview", async (HttpContext context, Guid organizationId, Guid workspaceId, BulkConnectPreviewRequest request, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.PreviewBulkConnectAsync(await actors.GetAsync(context, cancellationToken), organizationId, workspaceId, request, cancellationToken))));
        Protect(api.MapPost("/organizations/{organizationId:guid}/workspaces/{workspaceId:guid}/gateway/diagnostics", async (HttpContext context, Guid organizationId, Guid workspaceId, GatewayDiagnosticRequest request, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.DiagnoseGatewayAsync(await actors.GetAsync(context, cancellationToken), organizationId, workspaceId, request, cancellationToken))));

        api.MapGet("/organizations/{organizationId:guid}/service-accounts", async (HttpContext context, Guid organizationId, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListServiceAccountsAsync(await actors.GetAsync(context, cancellationToken), organizationId, cancellationToken)));
        Protect(api.MapPost("/organizations/{organizationId:guid}/service-accounts", async (HttpContext context, Guid organizationId, CreateServiceAccountRequest request, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
        {
            var created = await service.CreateServiceAccountAsync(await actors.GetAsync(context, cancellationToken), organizationId, request, cancellationToken);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Created($"/api/v1/organizations/{organizationId:D}/service-accounts/{created.Id:D}", created);
        }));
        Protect(api.MapDelete("/organizations/{organizationId:guid}/service-accounts/{serviceAccountId:guid}", async (HttpContext context, Guid organizationId, Guid serviceAccountId, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
        {
            await service.RevokeServiceAccountAsync(await actors.GetAsync(context, cancellationToken), organizationId, serviceAccountId, cancellationToken);
            return Results.NoContent();
        }));
        Protect(api.MapPost("/organizations/{organizationId:guid}/service-accounts/{serviceAccountId:guid}/rotate-secret", async (HttpContext context, Guid organizationId, Guid serviceAccountId, IPlatformActorAccessor actors, IPlatformManagementService service, CancellationToken cancellationToken) =>
        {
            var rotated = await service.RotateServiceAccountSecretAsync(await actors.GetAsync(context, cancellationToken), organizationId, serviceAccountId, cancellationToken);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(rotated);
        }));
        Protect(api.MapPost("/organizations/{organizationId:guid}/principals/{principalId:guid}/revoke", async (HttpContext context, Guid organizationId, Guid principalId, IPlatformActorAccessor actors, IPlatformSessionRevocationService service, CancellationToken cancellationToken) =>
        {
            await service.RevokePrincipalAsync(await actors.GetAsync(context, cancellationToken), organizationId, principalId, cancellationToken);
            return Results.NoContent();
        }));

        Protect(api.MapPost("/organizations/{organizationId:guid}/backup", async (HttpContext context, Guid organizationId, BackupRequest request, IPlatformActorAccessor actors, IAdministrativeBackupService service, CancellationToken cancellationToken) =>
        {
            var artifact = await service.CreateAsync(await actors.GetAsync(context, cancellationToken), organizationId, request, cancellationToken);
            context.Response.Headers.Append("X-Content-SHA256", artifact.Sha256);
            return Results.File(artifact.Content, "application/vnd.nextrdp.backup", artifact.FileName, enableRangeProcessing: false);
        }));
        Protect(api.MapPost("/administration/restore", async (HttpContext context, RestoreBackupRequest request, IPlatformActorAccessor actors, IAdministrativeBackupService service, CancellationToken cancellationToken) =>
        {
            byte[] content;
            try { content = Convert.FromBase64String(request.ContentBase64); }
            catch (FormatException) { throw PlatformProblemException.Validation(["Backup content must be valid base64."]); }
            return Results.Ok(await service.RestoreAsync(await actors.GetAsync(context, cancellationToken), content, request.Passphrase, cancellationToken));
        }));

        return endpoints;
    }

    private static RouteHandlerBuilder Protect(RouteHandlerBuilder builder) => builder.AddEndpointFilter<CookieAntiforgeryFilter>();

    private static IResult RevisionResult(HttpContext context, object value, long revision, int statusCode = StatusCodes.Status200OK, string? location = null)
    {
        context.Response.Headers.ETag = $"\"{revision}\"";
        if (statusCode == StatusCodes.Status201Created && location is not null) return Results.Created(location, value);
        return Results.Ok(value);
    }

    private static long RequiredRevision(HttpContext context)
    {
        var value = context.Request.Headers.IfMatch.ToString().Trim();
        if (value.StartsWith("W/", StringComparison.OrdinalIgnoreCase)) value = value[2..].Trim();
        value = value.Trim('"');
        if (!long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var revision) || revision < 1)
            throw new PlatformProblemException(428, "precondition_required", "Revision required", "Supply the current ETag in the If-Match header.");
        return revision;
    }

    private static long? OptionalRevision(HttpContext context)
    {
        if (!context.Request.Headers.ContainsKey("If-Match")) return null;
        return RequiredRevision(context);
    }

    private static string RequiredIdempotencyKey(HttpContext context)
    {
        var key = context.Request.Headers["Idempotency-Key"].ToString().Trim();
        if (key.Length is < 8 or > 128 || key.Any(char.IsControl))
            throw PlatformProblemException.Validation(["Idempotency-Key must contain 8 to 128 printable characters."]);
        return key;
    }
}
