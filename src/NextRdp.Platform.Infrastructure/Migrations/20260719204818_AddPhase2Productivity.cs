using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861

namespace NextRdp.Platform.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPhase2Productivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "dynamic_connection_groups",
                schema: "nextrdp",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RulesJson = table.Column<string>(type: "jsonb", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dynamic_connection_groups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_dynamic_connection_groups_workspaces_OrganizationId_Workspa~",
                        columns: x => new { x.OrganizationId, x.WorkspaceId },
                        principalSchema: "nextrdp",
                        principalTable: "workspaces",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "managed_connection_templates",
                schema: "nextrdp",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    TemplateJson = table.Column<string>(type: "jsonb", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_managed_connection_templates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_managed_connection_templates_workspaces_OrganizationId_Work~",
                        columns: x => new { x.OrganizationId, x.WorkspaceId },
                        principalSchema: "nextrdp",
                        principalTable: "workspaces",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_workspace_states",
                schema: "nextrdp",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PinnedFolderIdsJson = table.Column<string>(type: "jsonb", nullable: false),
                    LayoutJson = table.Column<string>(type: "jsonb", nullable: false),
                    SavedWorkspacesJson = table.Column<string>(type: "jsonb", nullable: false),
                    TabGroupsJson = table.Column<string>(type: "jsonb", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_workspace_states", x => new { x.OrganizationId, x.WorkspaceId, x.UserId });
                    table.ForeignKey(
                        name: "FK_user_workspace_states_users_UserId",
                        column: x => x.UserId,
                        principalSchema: "nextrdp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_user_workspace_states_workspaces_OrganizationId_WorkspaceId",
                        columns: x => new { x.OrganizationId, x.WorkspaceId },
                        principalSchema: "nextrdp",
                        principalTable: "workspaces",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_dynamic_connection_groups_OrganizationId_WorkspaceId_Name",
                schema: "nextrdp",
                table: "dynamic_connection_groups",
                columns: new[] { "OrganizationId", "WorkspaceId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_managed_connection_templates_OrganizationId_WorkspaceId_Name",
                schema: "nextrdp",
                table: "managed_connection_templates",
                columns: new[] { "OrganizationId", "WorkspaceId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_workspace_states_UserId",
                schema: "nextrdp",
                table: "user_workspace_states",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dynamic_connection_groups",
                schema: "nextrdp");

            migrationBuilder.DropTable(
                name: "managed_connection_templates",
                schema: "nextrdp");

            migrationBuilder.DropTable(
                name: "user_workspace_states",
                schema: "nextrdp");
        }
    }
}
