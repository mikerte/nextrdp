using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861

namespace NextRdp.Platform.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMembershipRevisionsAndWorkspaceScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Revision",
                schema: "nextrdp",
                table: "workspace_memberships",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddForeignKey(
                name: "FK_workspace_memberships_workspaces_OrganizationId_WorkspaceId",
                schema: "nextrdp",
                table: "workspace_memberships",
                columns: new[] { "OrganizationId", "WorkspaceId" },
                principalSchema: "nextrdp",
                principalTable: "workspaces",
                principalColumns: new[] { "OrganizationId", "Id" },
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_workspace_memberships_workspaces_OrganizationId_WorkspaceId",
                schema: "nextrdp",
                table: "workspace_memberships");

            migrationBuilder.DropColumn(
                name: "Revision",
                schema: "nextrdp",
                table: "workspace_memberships");
        }
    }
}
