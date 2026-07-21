using Microsoft.EntityFrameworkCore;
using NextRdp.Platform.Domain;
using OpenIddict.EntityFrameworkCore;

namespace NextRdp.Platform.Infrastructure;

public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<PlatformUser> Users => Set<PlatformUser>();
    public DbSet<WorkspaceMembership> Memberships => Set<WorkspaceMembership>();
    public DbSet<GroupRoleMapping> GroupRoleMappings => Set<GroupRoleMapping>();
    public DbSet<ManagedFolder> Folders => Set<ManagedFolder>();
    public DbSet<ManagedConnection> Connections => Set<ManagedConnection>();
    public DbSet<ConfigurationDraft> ConfigurationDrafts => Set<ConfigurationDraft>();
    public DbSet<PublishedConfiguration> PublishedConfigurations => Set<PublishedConfiguration>();
    public DbSet<PlatformAuditEvent> AuditEvents => Set<PlatformAuditEvent>();
    public DbSet<SyncChange> SyncChanges => Set<SyncChange>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<ServiceAccount> ServiceAccounts => Set<ServiceAccount>();
    public DbSet<ManagedImportPreviewRecord> ImportPreviews => Set<ManagedImportPreviewRecord>();
    public DbSet<DynamicConnectionGroup> DynamicGroups => Set<DynamicConnectionGroup>();
    public DbSet<ManagedConnectionTemplate> ConnectionTemplates => Set<ManagedConnectionTemplate>();
    public DbSet<UserWorkspaceState> UserWorkspaceStates => Set<UserWorkspaceState>();
    public DbSet<ProductSession> ProductSessions => Set<ProductSession>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceAppendOnlyAudit();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnforceAppendOnlyAudit();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema("nextrdp");
        ConfigureOrganizations(modelBuilder);
        ConfigureUsersAndMemberships(modelBuilder);
        ConfigureInventory(modelBuilder);
        ConfigureConfiguration(modelBuilder);
        ConfigureProductivity(modelBuilder);
        ConfigureOperations(modelBuilder);
        modelBuilder.UseOpenIddict<Guid>();
    }

    private static void ConfigureOrganizations(ModelBuilder modelBuilder)
    {
        var organizations = modelBuilder.Entity<Organization>();
        organizations.ToTable("organizations");
        organizations.HasKey(entity => entity.Id);
        organizations.Property(entity => entity.Name).HasMaxLength(200);
        organizations.Property(entity => entity.Slug).HasMaxLength(63);
        organizations.HasIndex(entity => entity.Slug).IsUnique();
        organizations.Property(entity => entity.Revision).IsConcurrencyToken();

        var workspaces = modelBuilder.Entity<Workspace>();
        workspaces.ToTable("workspaces");
        workspaces.HasKey(entity => entity.Id);
        workspaces.HasAlternateKey(entity => new { entity.OrganizationId, entity.Id });
        workspaces.HasOne<Organization>().WithMany().HasForeignKey(entity => entity.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        workspaces.Property(entity => entity.Name).HasMaxLength(200);
        workspaces.Property(entity => entity.Slug).HasMaxLength(63);
        workspaces.Property(entity => entity.Description).HasMaxLength(2000);
        workspaces.HasIndex(entity => new { entity.OrganizationId, entity.Slug }).IsUnique();
        workspaces.Property(entity => entity.Revision).IsConcurrencyToken();
    }

    private static void ConfigureUsersAndMemberships(ModelBuilder modelBuilder)
    {
        var users = modelBuilder.Entity<PlatformUser>();
        users.ToTable("users");
        users.HasKey(entity => entity.Id);
        users.Property(entity => entity.Issuer).HasMaxLength(2048);
        users.Property(entity => entity.Subject).HasMaxLength(255);
        users.Property(entity => entity.DisplayName).HasMaxLength(200);
        users.Property(entity => entity.EmailHint).HasMaxLength(320);
        users.HasIndex(entity => new { entity.Issuer, entity.Subject }).IsUnique();
        users.Property(entity => entity.SessionVersion).IsConcurrencyToken();

        var memberships = modelBuilder.Entity<WorkspaceMembership>();
        memberships.ToTable("workspace_memberships");
        memberships.HasKey(entity => entity.Id);
        memberships.HasOne<Organization>().WithMany().HasForeignKey(entity => entity.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        memberships.HasOne<PlatformUser>().WithMany().HasForeignKey(entity => entity.UserId).OnDelete(DeleteBehavior.Cascade);
        memberships.HasOne<Workspace>().WithMany().HasForeignKey(entity => new { entity.OrganizationId, entity.WorkspaceId })
            .HasPrincipalKey(entity => new { entity.OrganizationId, entity.Id }).OnDelete(DeleteBehavior.Cascade);
        memberships.Property(entity => entity.SourceGroup).HasMaxLength(512);
        memberships.HasIndex(entity => new { entity.OrganizationId, entity.WorkspaceId, entity.UserId, entity.Role }).IsUnique();
        memberships.HasIndex(entity => new { entity.UserId, entity.Status });
        memberships.Property(entity => entity.Revision).IsConcurrencyToken();

        var mappings = modelBuilder.Entity<GroupRoleMapping>();
        mappings.ToTable("group_role_mappings");
        mappings.HasKey(entity => entity.Id);
        mappings.HasOne<Organization>().WithMany().HasForeignKey(entity => entity.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        mappings.HasOne<Workspace>().WithMany().HasForeignKey(entity => new { entity.OrganizationId, entity.WorkspaceId })
            .HasPrincipalKey(entity => new { entity.OrganizationId, entity.Id }).OnDelete(DeleteBehavior.Cascade);
        mappings.Property(entity => entity.GroupClaim).HasMaxLength(512);
        mappings.Property(entity => entity.Revision).IsConcurrencyToken();
        mappings.HasIndex(entity => new { entity.OrganizationId, entity.WorkspaceId, entity.GroupClaim, entity.Role }).IsUnique();
    }

    private static void ConfigureInventory(ModelBuilder modelBuilder)
    {
        var folders = modelBuilder.Entity<ManagedFolder>();
        folders.ToTable("managed_folders");
        folders.HasKey(entity => entity.Id);
        folders.HasAlternateKey(entity => new { entity.OrganizationId, entity.WorkspaceId, entity.Id });
        folders.HasOne<Workspace>().WithMany().HasForeignKey(entity => new { entity.OrganizationId, entity.WorkspaceId }).HasPrincipalKey(entity => new { entity.OrganizationId, entity.Id }).OnDelete(DeleteBehavior.Cascade);
        folders.Property(entity => entity.Name).HasMaxLength(200);
        folders.Property(entity => entity.Revision).IsConcurrencyToken();
        folders.HasIndex(entity => new { entity.OrganizationId, entity.WorkspaceId, entity.ParentId, entity.Name }).IsUnique();

        var connections = modelBuilder.Entity<ManagedConnection>();
        connections.ToTable("managed_connections", table => table.HasCheckConstraint("ck_connection_personal_owner", "\"Visibility\" <> 0 OR \"OwnerUserId\" IS NOT NULL"));
        connections.HasKey(entity => entity.Id);
        connections.HasOne<Workspace>().WithMany().HasForeignKey(entity => new { entity.OrganizationId, entity.WorkspaceId }).HasPrincipalKey(entity => new { entity.OrganizationId, entity.Id }).OnDelete(DeleteBehavior.Cascade);
        connections.Property(entity => entity.Name).HasMaxLength(200);
        connections.Property(entity => entity.Host).HasMaxLength(255);
        connections.Property(entity => entity.UsernameHint).HasMaxLength(320);
        connections.Property(entity => entity.ProvisioningKey).HasMaxLength(200);
        connections.Property(entity => entity.Notes).HasMaxLength(4000);
        connections.Property(entity => entity.Tags).HasColumnType("text[]");
        connections.OwnsOne(entity => entity.Metadata, owned => owned.ToJson("metadata"));
        connections.OwnsOne(entity => entity.Settings, owned => owned.ToJson("rdp_settings"));
        connections.Property(entity => entity.Revision).IsConcurrencyToken();
        connections.HasIndex(entity => new { entity.OrganizationId, entity.WorkspaceId, entity.Name });
        connections.HasIndex(entity => new { entity.OrganizationId, entity.WorkspaceId, entity.Host, entity.Port });
        connections.HasIndex(entity => new { entity.OrganizationId, entity.WorkspaceId, entity.FolderId });
        connections.HasIndex(entity => new { entity.OrganizationId, entity.WorkspaceId, entity.OwnerUserId, entity.Visibility });
        connections.HasIndex(entity => new { entity.OrganizationId, entity.WorkspaceId, entity.ProvisioningKey })
            .IsUnique()
            .HasFilter("\"ProvisioningKey\" <> ''");
    }

    private static void ConfigureConfiguration(ModelBuilder modelBuilder)
    {
        var drafts = modelBuilder.Entity<ConfigurationDraft>();
        drafts.ToTable("configuration_drafts");
        drafts.HasKey(entity => entity.Id);
        drafts.HasOne<Organization>().WithMany().HasForeignKey(entity => entity.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        drafts.Property(entity => entity.ValuesJson).HasColumnType("jsonb");
        drafts.Property(entity => entity.LockedFieldsJson).HasColumnType("jsonb");
        drafts.Property(entity => entity.Summary).HasMaxLength(500);
        drafts.Property(entity => entity.Revision).IsConcurrencyToken();
        drafts.HasIndex(entity => new { entity.OrganizationId, entity.WorkspaceId, entity.ScopeType, entity.ScopeId, entity.Lifecycle });

        var published = modelBuilder.Entity<PublishedConfiguration>();
        published.ToTable("published_configurations");
        published.HasKey(entity => entity.Id);
        published.HasOne<Organization>().WithMany().HasForeignKey(entity => entity.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        published.Property(entity => entity.ValuesJson).HasColumnType("jsonb");
        published.Property(entity => entity.LockedFieldsJson).HasColumnType("jsonb");
        published.Property(entity => entity.Summary).HasMaxLength(500);
        published.HasIndex(entity => new { entity.OrganizationId, entity.WorkspaceId, entity.ScopeType, entity.ScopeId, entity.Version }).IsUnique();
    }

    private static void ConfigureOperations(ModelBuilder modelBuilder)
    {
        var audits = modelBuilder.Entity<PlatformAuditEvent>();
        audits.ToTable("audit_events");
        audits.HasKey(entity => entity.Id);
        audits.Property(entity => entity.Action).HasMaxLength(200);
        audits.Property(entity => entity.ResourceType).HasMaxLength(100);
        audits.Property(entity => entity.RedactedDetailJson).HasColumnType("jsonb");
        audits.Property(entity => entity.CorrelationId).HasMaxLength(64);
        audits.HasIndex(entity => new { entity.OrganizationId, entity.WorkspaceId, entity.OccurredAtUtc });
        audits.HasIndex(entity => entity.CorrelationId);

        var sync = modelBuilder.Entity<SyncChange>();
        sync.ToTable("sync_changes");
        sync.HasKey(entity => entity.Sequence);
        sync.Property(entity => entity.Sequence).UseIdentityAlwaysColumn();
        sync.Property(entity => entity.ResourceType).HasMaxLength(100);
        sync.Property(entity => entity.PayloadJson).HasColumnType("jsonb");
        sync.HasIndex(entity => new { entity.OrganizationId, entity.WorkspaceId, entity.Sequence });
        sync.HasIndex(entity => new { entity.OrganizationId, entity.WorkspaceId, entity.AudienceUserId, entity.Sequence });

        var outbox = modelBuilder.Entity<OutboxMessage>();
        outbox.ToTable("outbox_messages");
        outbox.HasKey(entity => entity.Id);
        outbox.Property(entity => entity.Type).HasMaxLength(200);
        outbox.Property(entity => entity.PayloadJson).HasColumnType("jsonb");
        outbox.Property(entity => entity.LastErrorCode).HasMaxLength(100);
        outbox.HasIndex(entity => new { entity.ProcessedAtUtc, entity.NextAttemptAtUtc });

        var idempotency = modelBuilder.Entity<IdempotencyRecord>();
        idempotency.ToTable("idempotency_records");
        idempotency.HasKey(entity => new { entity.OrganizationId, entity.ActorKey, entity.IdempotencyKey });
        idempotency.Property(entity => entity.ActorKey).HasMaxLength(300);
        idempotency.Property(entity => entity.IdempotencyKey).HasMaxLength(200);
        idempotency.Property(entity => entity.RequestHash).HasMaxLength(64);
        idempotency.Property(entity => entity.ResponseJson).HasColumnType("jsonb");
        idempotency.HasIndex(entity => entity.ExpiresAtUtc);

        var serviceAccounts = modelBuilder.Entity<ServiceAccount>();
        serviceAccounts.ToTable("service_accounts");
        serviceAccounts.HasKey(entity => entity.Id);
        serviceAccounts.HasOne<Organization>().WithMany().HasForeignKey(entity => entity.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        serviceAccounts.Property(entity => entity.Name).HasMaxLength(200);
        serviceAccounts.Property(entity => entity.ClientId).HasMaxLength(200);
        serviceAccounts.Property(entity => entity.ScopesJson).HasColumnType("jsonb");
        serviceAccounts.HasIndex(entity => entity.ClientId).IsUnique();
        serviceAccounts.HasIndex(entity => new { entity.OrganizationId, entity.WorkspaceId, entity.RevokedAtUtc });

        var importPreviews = modelBuilder.Entity<ManagedImportPreviewRecord>();
        importPreviews.ToTable("managed_import_previews");
        importPreviews.HasKey(entity => entity.Id);
        importPreviews.Property(entity => entity.CandidatesJson).HasColumnType("jsonb");
        importPreviews.HasIndex(entity => new { entity.OrganizationId, entity.WorkspaceId, entity.ActorId, entity.ExpiresAtUtc });

        var sessions = modelBuilder.Entity<ProductSession>();
        sessions.ToTable("product_sessions");
        sessions.HasKey(entity => entity.Id);
        sessions.HasOne<PlatformUser>().WithMany().HasForeignKey(entity => entity.UserId).OnDelete(DeleteBehavior.Cascade);
        sessions.Property(entity => entity.ClientId).HasMaxLength(200);
        sessions.HasIndex(entity => new { entity.UserId, entity.RevokedAtUtc, entity.AbsoluteExpiresAtUtc });
    }

    private static void ConfigureProductivity(ModelBuilder modelBuilder)
    {
        var dynamicGroups = modelBuilder.Entity<DynamicConnectionGroup>();
        dynamicGroups.ToTable("dynamic_connection_groups");
        dynamicGroups.HasKey(entity => entity.Id);
        dynamicGroups.HasOne<Workspace>().WithMany().HasForeignKey(entity => new { entity.OrganizationId, entity.WorkspaceId })
            .HasPrincipalKey(entity => new { entity.OrganizationId, entity.Id }).OnDelete(DeleteBehavior.Cascade);
        dynamicGroups.Property(entity => entity.Name).HasMaxLength(200);
        dynamicGroups.Property(entity => entity.RulesJson).HasColumnType("jsonb");
        dynamicGroups.Property(entity => entity.Revision).IsConcurrencyToken();
        dynamicGroups.HasIndex(entity => new { entity.OrganizationId, entity.WorkspaceId, entity.Name }).IsUnique();

        var templates = modelBuilder.Entity<ManagedConnectionTemplate>();
        templates.ToTable("managed_connection_templates");
        templates.HasKey(entity => entity.Id);
        templates.HasOne<Workspace>().WithMany().HasForeignKey(entity => new { entity.OrganizationId, entity.WorkspaceId })
            .HasPrincipalKey(entity => new { entity.OrganizationId, entity.Id }).OnDelete(DeleteBehavior.Cascade);
        templates.Property(entity => entity.Name).HasMaxLength(200);
        templates.Property(entity => entity.Description).HasMaxLength(1000);
        templates.Property(entity => entity.TemplateJson).HasColumnType("jsonb");
        templates.Property(entity => entity.Revision).IsConcurrencyToken();
        templates.HasIndex(entity => new { entity.OrganizationId, entity.WorkspaceId, entity.Name }).IsUnique();

        var userStates = modelBuilder.Entity<UserWorkspaceState>();
        userStates.ToTable("user_workspace_states");
        userStates.HasKey(entity => new { entity.OrganizationId, entity.WorkspaceId, entity.UserId });
        userStates.HasOne<Workspace>().WithMany().HasForeignKey(entity => new { entity.OrganizationId, entity.WorkspaceId })
            .HasPrincipalKey(entity => new { entity.OrganizationId, entity.Id }).OnDelete(DeleteBehavior.Cascade);
        userStates.HasOne<PlatformUser>().WithMany().HasForeignKey(entity => entity.UserId).OnDelete(DeleteBehavior.Cascade);
        userStates.Property(entity => entity.PinnedFolderIdsJson).HasColumnType("jsonb");
        userStates.Property(entity => entity.LayoutJson).HasColumnType("jsonb");
        userStates.Property(entity => entity.SavedWorkspacesJson).HasColumnType("jsonb");
        userStates.Property(entity => entity.TabGroupsJson).HasColumnType("jsonb");
        userStates.Property(entity => entity.Revision).IsConcurrencyToken();
    }

    private void EnforceAppendOnlyAudit()
    {
        if (ChangeTracker.Entries<PlatformAuditEvent>().Any(entry => entry.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Platform audit events are append-only.");
    }
}
