using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#pragma warning disable CA1861 // EF-generated migration uses inline column arrays.

#nullable disable

namespace NextRdp.Platform.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialPlatformSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "nextrdp");

            migrationBuilder.CreateTable(
                name: "audit_events",
                schema: "nextrdp",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ServiceAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    Action = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ResourceType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ResourceId = table.Column<Guid>(type: "uuid", nullable: true),
                    Outcome = table.Column<int>(type: "integer", nullable: false),
                    RedactedDetailJson = table.Column<string>(type: "jsonb", nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "idempotency_records",
                schema: "nextrdp",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StatusCode = table.Column<int>(type: "integer", nullable: false),
                    ResponseJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_idempotency_records", x => new { x.OrganizationId, x.ActorKey, x.IdempotencyKey });
                });

            migrationBuilder.CreateTable(
                name: "organizations",
                schema: "nextrdp",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Slug = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organizations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "nextrdp",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProcessedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_messages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sync_changes",
                schema: "nextrdp",
                columns: table => new
                {
                    Sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResourceType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ResourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    ResourceRevision = table.Column<long>(type: "bigint", nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_changes", x => x.Sequence);
                });

            migrationBuilder.CreateTable(
                name: "users",
                schema: "nextrdp",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Issuer = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Subject = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EmailHint = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    LastAuthenticatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SessionVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "configuration_drafts",
                schema: "nextrdp",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: true),
                    ScopeType = table.Column<int>(type: "integer", nullable: false),
                    ScopeId = table.Column<Guid>(type: "uuid", nullable: false),
                    BaseVersion = table.Column<int>(type: "integer", nullable: false),
                    ValuesJson = table.Column<string>(type: "jsonb", nullable: false),
                    LockedFieldsJson = table.Column<string>(type: "jsonb", nullable: false),
                    Lifecycle = table.Column<int>(type: "integer", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_configuration_drafts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_configuration_drafts_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "nextrdp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "published_configurations",
                schema: "nextrdp",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: true),
                    ScopeType = table.Column<int>(type: "integer", nullable: false),
                    ScopeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    SourceDraftId = table.Column<Guid>(type: "uuid", nullable: false),
                    RolledBackFromId = table.Column<Guid>(type: "uuid", nullable: true),
                    ValuesJson = table.Column<string>(type: "jsonb", nullable: false),
                    LockedFieldsJson = table.Column<string>(type: "jsonb", nullable: false),
                    PublishedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    PublishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_published_configurations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_published_configurations_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "nextrdp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "service_accounts",
                schema: "nextrdp",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ClientId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ScopesJson = table.Column<string>(type: "jsonb", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_accounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_service_accounts_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "nextrdp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workspaces",
                schema: "nextrdp",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Slug = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workspaces", x => x.Id);
                    table.UniqueConstraint("AK_workspaces_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.ForeignKey(
                        name: "FK_workspaces_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "nextrdp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workspace_memberships",
                schema: "nextrdp",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    SourceGroup = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workspace_memberships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_workspace_memberships_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "nextrdp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_workspace_memberships_users_UserId",
                        column: x => x.UserId,
                        principalSchema: "nextrdp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "managed_connections",
                schema: "nextrdp",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    FolderId = table.Column<Guid>(type: "uuid", nullable: true),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Visibility = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Port = table.Column<int>(type: "integer", nullable: false),
                    UsernameHint = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    Environment = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Tags = table.Column<List<string>>(type: "text[]", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    metadata = table.Column<string>(type: "jsonb", nullable: false),
                    rdp_settings = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_managed_connections", x => x.Id);
                    table.CheckConstraint("ck_connection_personal_owner", "\"Visibility\" <> 0 OR \"OwnerUserId\" IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_managed_connections_workspaces_OrganizationId_WorkspaceId",
                        columns: x => new { x.OrganizationId, x.WorkspaceId },
                        principalSchema: "nextrdp",
                        principalTable: "workspaces",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "managed_folders",
                schema: "nextrdp",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_managed_folders", x => x.Id);
                    table.UniqueConstraint("AK_managed_folders_OrganizationId_WorkspaceId_Id", x => new { x.OrganizationId, x.WorkspaceId, x.Id });
                    table.ForeignKey(
                        name: "FK_managed_folders_workspaces_OrganizationId_WorkspaceId",
                        columns: x => new { x.OrganizationId, x.WorkspaceId },
                        principalSchema: "nextrdp",
                        principalTable: "workspaces",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_CorrelationId",
                schema: "nextrdp",
                table: "audit_events",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_OrganizationId_WorkspaceId_OccurredAtUtc",
                schema: "nextrdp",
                table: "audit_events",
                columns: new[] { "OrganizationId", "WorkspaceId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_configuration_drafts_OrganizationId_WorkspaceId_ScopeType_S~",
                schema: "nextrdp",
                table: "configuration_drafts",
                columns: new[] { "OrganizationId", "WorkspaceId", "ScopeType", "ScopeId", "Lifecycle" });

            migrationBuilder.CreateIndex(
                name: "IX_idempotency_records_ExpiresAtUtc",
                schema: "nextrdp",
                table: "idempotency_records",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_managed_connections_OrganizationId_WorkspaceId_FolderId",
                schema: "nextrdp",
                table: "managed_connections",
                columns: new[] { "OrganizationId", "WorkspaceId", "FolderId" });

            migrationBuilder.CreateIndex(
                name: "IX_managed_connections_OrganizationId_WorkspaceId_Host_Port",
                schema: "nextrdp",
                table: "managed_connections",
                columns: new[] { "OrganizationId", "WorkspaceId", "Host", "Port" });

            migrationBuilder.CreateIndex(
                name: "IX_managed_connections_OrganizationId_WorkspaceId_Name",
                schema: "nextrdp",
                table: "managed_connections",
                columns: new[] { "OrganizationId", "WorkspaceId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_managed_connections_OrganizationId_WorkspaceId_OwnerUserId_~",
                schema: "nextrdp",
                table: "managed_connections",
                columns: new[] { "OrganizationId", "WorkspaceId", "OwnerUserId", "Visibility" });

            migrationBuilder.CreateIndex(
                name: "IX_managed_folders_OrganizationId_WorkspaceId_ParentId_Name",
                schema: "nextrdp",
                table: "managed_folders",
                columns: new[] { "OrganizationId", "WorkspaceId", "ParentId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_organizations_Slug",
                schema: "nextrdp",
                table: "organizations",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_ProcessedAtUtc_NextAttemptAtUtc",
                schema: "nextrdp",
                table: "outbox_messages",
                columns: new[] { "ProcessedAtUtc", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_published_configurations_OrganizationId_WorkspaceId_ScopeTy~",
                schema: "nextrdp",
                table: "published_configurations",
                columns: new[] { "OrganizationId", "WorkspaceId", "ScopeType", "ScopeId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_service_accounts_ClientId",
                schema: "nextrdp",
                table: "service_accounts",
                column: "ClientId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_service_accounts_OrganizationId_WorkspaceId_RevokedAtUtc",
                schema: "nextrdp",
                table: "service_accounts",
                columns: new[] { "OrganizationId", "WorkspaceId", "RevokedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_sync_changes_OrganizationId_WorkspaceId_Sequence",
                schema: "nextrdp",
                table: "sync_changes",
                columns: new[] { "OrganizationId", "WorkspaceId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_users_Issuer_Subject",
                schema: "nextrdp",
                table: "users",
                columns: new[] { "Issuer", "Subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workspace_memberships_OrganizationId_WorkspaceId_UserId_Role",
                schema: "nextrdp",
                table: "workspace_memberships",
                columns: new[] { "OrganizationId", "WorkspaceId", "UserId", "Role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workspace_memberships_UserId_Status",
                schema: "nextrdp",
                table: "workspace_memberships",
                columns: new[] { "UserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_workspaces_OrganizationId_Slug",
                schema: "nextrdp",
                table: "workspaces",
                columns: new[] { "OrganizationId", "Slug" },
                unique: true);

            migrationBuilder.Sql(
                """
                CREATE FUNCTION nextrdp.reject_audit_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $body$
                BEGIN
                    RAISE EXCEPTION USING ERRCODE = '55000', MESSAGE = 'NextRDP audit events are append-only';
                END;
                $body$;

                CREATE TRIGGER audit_events_append_only
                BEFORE UPDATE OR DELETE ON nextrdp.audit_events
                FOR EACH ROW EXECUTE FUNCTION nextrdp.reject_audit_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS audit_events_append_only ON nextrdp.audit_events;
                DROP FUNCTION IF EXISTS nextrdp.reject_audit_mutation();
                """);

            migrationBuilder.DropTable(
                name: "audit_events",
                schema: "nextrdp");

            migrationBuilder.DropTable(
                name: "configuration_drafts",
                schema: "nextrdp");

            migrationBuilder.DropTable(
                name: "idempotency_records",
                schema: "nextrdp");

            migrationBuilder.DropTable(
                name: "managed_connections",
                schema: "nextrdp");

            migrationBuilder.DropTable(
                name: "managed_folders",
                schema: "nextrdp");

            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "nextrdp");

            migrationBuilder.DropTable(
                name: "published_configurations",
                schema: "nextrdp");

            migrationBuilder.DropTable(
                name: "service_accounts",
                schema: "nextrdp");

            migrationBuilder.DropTable(
                name: "sync_changes",
                schema: "nextrdp");

            migrationBuilder.DropTable(
                name: "workspace_memberships",
                schema: "nextrdp");

            migrationBuilder.DropTable(
                name: "workspaces",
                schema: "nextrdp");

            migrationBuilder.DropTable(
                name: "users",
                schema: "nextrdp");

            migrationBuilder.DropTable(
                name: "organizations",
                schema: "nextrdp");
        }
    }
}
