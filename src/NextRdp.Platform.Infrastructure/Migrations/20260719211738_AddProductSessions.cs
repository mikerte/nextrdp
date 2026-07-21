using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861

namespace NextRdp.Platform.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProductSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "product_sessions",
                schema: "nextrdp",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionVersion = table.Column<long>(type: "bigint", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastActivityAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IdleExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AbsoluteExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IdleTimeoutMinutes = table.Column<int>(type: "integer", nullable: false),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ClientId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_product_sessions_users_UserId",
                        column: x => x.UserId,
                        principalSchema: "nextrdp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_product_sessions_UserId_RevokedAtUtc_AbsoluteExpiresAtUtc",
                schema: "nextrdp",
                table: "product_sessions",
                columns: new[] { "UserId", "RevokedAtUtc", "AbsoluteExpiresAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "product_sessions",
                schema: "nextrdp");
        }
    }
}
