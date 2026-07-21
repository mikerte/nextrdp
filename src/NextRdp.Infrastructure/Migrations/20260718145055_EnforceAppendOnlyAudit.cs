using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NextRdp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EnforceAppendOnlyAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TRIGGER IF NOT EXISTS "TR_AuditEvents_RejectUpdate"
                BEFORE UPDATE ON "AuditEvents"
                BEGIN
                    SELECT RAISE(ABORT, 'Audit events are append-only.');
                END;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER IF NOT EXISTS "TR_AuditEvents_RejectDelete"
                BEFORE DELETE ON "AuditEvents"
                BEGIN
                    SELECT RAISE(ABORT, 'Audit events are append-only.');
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS \"TR_AuditEvents_RejectUpdate\";");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS \"TR_AuditEvents_RejectDelete\";");
        }
    }
}
