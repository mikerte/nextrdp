using System;
using Microsoft.EntityFrameworkCore.Migrations;

#pragma warning disable CA1861 // EF-generated migration uses inline column arrays.

#nullable disable

namespace NextRdp.Platform.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ScopeSyncChangesToAudience : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AudienceUserId",
                schema: "nextrdp",
                table: "sync_changes",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_sync_changes_OrganizationId_WorkspaceId_AudienceUserId_Sequ~",
                schema: "nextrdp",
                table: "sync_changes",
                columns: new[] { "OrganizationId", "WorkspaceId", "AudienceUserId", "Sequence" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_sync_changes_OrganizationId_WorkspaceId_AudienceUserId_Sequ~",
                schema: "nextrdp",
                table: "sync_changes");

            migrationBuilder.DropColumn(
                name: "AudienceUserId",
                schema: "nextrdp",
                table: "sync_changes");
        }
    }
}
