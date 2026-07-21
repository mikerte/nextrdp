using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NextRdp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialLocalSchema : Migration
    {
        private static readonly string[] ConnectionTargetColumns = ["Host", "Port"];
        private static readonly string[] FolderIdentityColumns = ["ParentId", "Name"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    RedactedDetail = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Connections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FolderId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Host = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    UsernameHint = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    Environment = table.Column<int>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    IsFavorite = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastConnectedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Tags = table.Column<string>(type: "TEXT", nullable: false),
                    Settings = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Connections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Folders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ParentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Folders", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_OccurredAtUtc",
                table: "AuditEvents",
                column: "OccurredAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Connections_Host_Port",
                table: "Connections",
                columns: ConnectionTargetColumns);

            migrationBuilder.CreateIndex(
                name: "IX_Folders_ParentId_Name",
                table: "Folders",
                columns: FolderIdentityColumns,
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditEvents");

            migrationBuilder.DropTable(
                name: "Connections");

            migrationBuilder.DropTable(
                name: "Folders");
        }
    }
}
