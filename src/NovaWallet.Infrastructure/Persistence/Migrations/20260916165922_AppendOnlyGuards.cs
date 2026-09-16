using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NovaWallet.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Makes the audit log and the ledger itself append-only at the database level: any UPDATE, DELETE or
    /// TRUNCATE raises an error regardless of which client issues it. In production the application's DB role
    /// should additionally be granted only SELECT/INSERT on these tables, since a table owner can disable triggers.
    /// </summary>
    public partial class AppendOnlyGuards : Migration
    {
        private static readonly string[] AppendOnlyTables = ["audit_log", "ledger_entries", "ledger_transactions"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION reject_append_only_mutation() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION '% is append-only: % is not allowed', TG_TABLE_NAME, TG_OP
                        USING ERRCODE = 'insufficient_privilege';
                END;
                $$;
                """);

            foreach (var table in AppendOnlyTables)
            {
                migrationBuilder.Sql(
                    $"""
                     CREATE TRIGGER {table}_no_update_delete
                         BEFORE UPDATE OR DELETE ON {table}
                         FOR EACH ROW EXECUTE FUNCTION reject_append_only_mutation();
                     CREATE TRIGGER {table}_no_truncate
                         BEFORE TRUNCATE ON {table}
                         FOR EACH STATEMENT EXECUTE FUNCTION reject_append_only_mutation();
                     """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in AppendOnlyTables)
            {
                migrationBuilder.Sql(
                    $"""
                     DROP TRIGGER IF EXISTS {table}_no_update_delete ON {table};
                     DROP TRIGGER IF EXISTS {table}_no_truncate ON {table};
                     """);
            }

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS reject_append_only_mutation();");
        }
    }
}
