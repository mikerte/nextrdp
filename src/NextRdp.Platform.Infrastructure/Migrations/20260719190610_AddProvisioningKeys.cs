using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // EF-generated migration uses inline column arrays.

namespace NextRdp.Platform.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProvisioningKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProvisioningKey",
                schema: "nextrdp",
                table: "managed_connections",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_managed_connections_OrganizationId_WorkspaceId_Provisioning~",
                schema: "nextrdp",
                table: "managed_connections",
                columns: new[] { "OrganizationId", "WorkspaceId", "ProvisioningKey" },
                unique: true,
                filter: "\"ProvisioningKey\" <> ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_managed_connections_OrganizationId_WorkspaceId_Provisioning~",
                schema: "nextrdp",
                table: "managed_connections");

            migrationBuilder.DropColumn(
                name: "ProvisioningKey",
                schema: "nextrdp",
                table: "managed_connections");
        }
    }
}
