using NextRdp.Platform.Domain;

namespace NextRdp.Platform.Application;

public interface IPlatformManagementService
{
    Task<IReadOnlyList<OrganizationDto>> ListOrganizationsAsync(PlatformActor actor, CancellationToken cancellationToken = default);
    Task<OrganizationDto> CreateOrganizationAsync(PlatformActor actor, CreateOrganizationRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkspaceDto>> ListWorkspacesAsync(PlatformActor actor, Guid organizationId, CancellationToken cancellationToken = default);
    Task<WorkspaceDto> CreateWorkspaceAsync(PlatformActor actor, Guid organizationId, CreateWorkspaceRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PlatformUserDto>> ListUsersAsync(PlatformActor actor, Guid organizationId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MembershipDto>> ListMembershipsAsync(PlatformActor actor, Guid organizationId, Guid? workspaceId, CancellationToken cancellationToken = default);
    Task<MembershipDto> SaveMembershipAsync(PlatformActor actor, Guid organizationId, Guid? membershipId, UpsertMembershipRequest request, CancellationToken cancellationToken = default);
    Task RevokeMembershipAsync(PlatformActor actor, Guid organizationId, Guid membershipId, long expectedRevision, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GroupRoleMappingDto>> ListGroupRoleMappingsAsync(PlatformActor actor, Guid organizationId, CancellationToken cancellationToken = default);
    Task<GroupRoleMappingDto> SaveGroupRoleMappingAsync(PlatformActor actor, Guid organizationId, Guid? mappingId, SaveGroupRoleMappingRequest request, CancellationToken cancellationToken = default);
    Task DeleteGroupRoleMappingAsync(PlatformActor actor, Guid organizationId, Guid mappingId, long expectedRevision, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FolderDto>> ListFoldersAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, CancellationToken cancellationToken = default);
    Task<FolderDto> SaveFolderAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, Guid? folderId, UpsertFolderRequest request, CancellationToken cancellationToken = default);
    Task<PagedResult<ManagedConnectionDto>> ListConnectionsAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, string? search, string? cursor, int pageSize, CancellationToken cancellationToken = default);
    Task<ManagedConnectionDto> GetConnectionAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, Guid connectionId, CancellationToken cancellationToken = default);
    Task<ManagedConnectionDto> SaveConnectionAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, Guid? connectionId, UpsertManagedConnectionRequest request, CancellationToken cancellationToken = default);
    Task DeleteConnectionAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, Guid connectionId, long expectedRevision, CancellationToken cancellationToken = default);
    Task<ConfigurationDraftDto> SaveConfigurationDraftAsync(PlatformActor actor, Guid organizationId, Guid? workspaceId, Guid? draftId, SaveConfigurationDraftRequest request, CancellationToken cancellationToken = default);
    Task<PublishedConfigurationDto> PublishConfigurationAsync(PlatformActor actor, Guid organizationId, Guid? workspaceId, Guid draftId, CancellationToken cancellationToken = default);
    Task<PublishedConfigurationDto> RollbackConfigurationAsync(PlatformActor actor, Guid organizationId, Guid? workspaceId, Guid publishedConfigurationId, string summary, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PublishedConfigurationDto>> ListConfigurationHistoryAsync(PlatformActor actor, Guid organizationId, Guid? workspaceId, ConfigurationScopeType scopeType, Guid scopeId, CancellationToken cancellationToken = default);
    Task<EffectivePolicy> GetEffectivePolicyAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, Guid? folderId, Guid? connectionId, CancellationToken cancellationToken = default);
    Task<SyncResponse> SynchronizeAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, SyncRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AuditEventDto>> ListAuditAsync(PlatformActor actor, Guid organizationId, Guid? workspaceId, int limit, CancellationToken cancellationToken = default);
    Task<BulkProvisionResult> BulkProvisionAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, string idempotencyKey, BulkProvisionRequest request, CancellationToken cancellationToken = default);
    Task<ManagedImportPreview> PreviewImportAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, ImportPreviewRequest request, CancellationToken cancellationToken = default);
    Task<BulkProvisionResult> CommitImportAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, CommitImportRequest request, CancellationToken cancellationToken = default);
    Task<ExportEnvelope> ExportInventoryAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ServiceAccountDto>> ListServiceAccountsAsync(PlatformActor actor, Guid organizationId, CancellationToken cancellationToken = default);
    Task<CreatedServiceAccount> CreateServiceAccountAsync(PlatformActor actor, Guid organizationId, CreateServiceAccountRequest request, CancellationToken cancellationToken = default);
    Task<RotatedServiceAccountSecret> RotateServiceAccountSecretAsync(PlatformActor actor, Guid organizationId, Guid serviceAccountId, CancellationToken cancellationToken = default);
    Task RevokeServiceAccountAsync(PlatformActor actor, Guid organizationId, Guid serviceAccountId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DynamicGroupDto>> ListDynamicGroupsAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, CancellationToken cancellationToken = default);
    Task<DynamicGroupDto> SaveDynamicGroupAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, Guid? groupId, SaveDynamicGroupRequest request, CancellationToken cancellationToken = default);
    Task DeleteDynamicGroupAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, Guid groupId, long expectedRevision, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ConnectionTemplateDto>> ListConnectionTemplatesAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, CancellationToken cancellationToken = default);
    Task<ConnectionTemplateDto> SaveConnectionTemplateAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, Guid? templateId, SaveConnectionTemplateRequest request, CancellationToken cancellationToken = default);
    Task<ManagedConnectionDto> CreateConnectionFromTemplateAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, CreateConnectionFromTemplateRequest request, CancellationToken cancellationToken = default);
    Task<ManagedConnectionDto> CopyConnectionAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, Guid connectionId, CopyManagedConnectionRequest request, CancellationToken cancellationToken = default);
    Task<UserWorkspaceStateDto> GetUserWorkspaceStateAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, CancellationToken cancellationToken = default);
    Task<UserWorkspaceStateDto> SaveUserWorkspaceStateAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, SaveUserWorkspaceStateRequest request, CancellationToken cancellationToken = default);
    Task<BulkProvisionResult> BulkEditConnectionsAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, string idempotencyKey, BulkConnectionEditRequest request, CancellationToken cancellationToken = default);
    Task<BulkConnectPreviewResult> PreviewBulkConnectAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, BulkConnectPreviewRequest request, CancellationToken cancellationToken = default);
    Task<GatewayDiagnosticResult> DiagnoseGatewayAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, GatewayDiagnosticRequest request, CancellationToken cancellationToken = default);
}

public interface IAdministrativeBackupService
{
    Task<BackupArtifact> CreateAsync(PlatformActor actor, Guid organizationId, BackupRequest request, CancellationToken cancellationToken = default);
    Task<RestoreResult> RestoreAsync(PlatformActor actor, ReadOnlyMemory<byte> content, string passphrase, CancellationToken cancellationToken = default);
}

public interface IPlatformSessionRevocationService
{
    Task RevokePrincipalAsync(PlatformActor actor, Guid organizationId, Guid principalId, CancellationToken cancellationToken = default);
    Task RevokeSelfAsync(PlatformActor actor, CancellationToken cancellationToken = default);
    Task<bool> IsCurrentAsync(Guid principalId, long sessionVersion, CancellationToken cancellationToken = default);
}

public interface ISyncCursorCodec
{
    string Encode(long sequence);
    long Decode(string? cursor);
}

public interface IServiceAccountCredentialStore
{
    Task StoreAsync(string clientId, string clientSecret, IReadOnlyList<string> scopes, DateTimeOffset? expiresAtUtc, CancellationToken cancellationToken = default);
    Task RotateAsync(string clientId, string clientSecret, IReadOnlyList<string> scopes, DateTimeOffset? expiresAtUtc, CancellationToken cancellationToken = default);
    Task RevokeAsync(string clientId, CancellationToken cancellationToken = default);
}
