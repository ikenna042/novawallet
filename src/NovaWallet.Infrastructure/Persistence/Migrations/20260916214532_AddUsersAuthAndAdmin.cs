using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NovaWallet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUsersAuthAndAdmin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "frozen_at",
                table: "wallets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "frozen_reason",
                table: "wallets",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "wallets",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Active"); // existing wallets are active

            migrationBuilder.CreateTable(
                name: "admin_actions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    actor_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    target_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    target_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    detail = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_admin_actions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    full_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    password_hash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    token_version = table.Column<int>(type: "integer", nullable: false),
                    failed_login_count = table.Column<int>(type: "integer", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    disabled_reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                    table.CheckConstraint("ck_users_email_normalized", "email = lower(email)");
                    table.CheckConstraint("ck_users_token_version_positive", "token_version >= 1");
                });

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    replaced_by_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refresh_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_refresh_tokens_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_wallets_frozen_has_reason",
                table: "wallets",
                sql: "status <> 'Frozen' OR frozen_reason IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_admin_actions_target_type_target_id",
                table: "admin_actions",
                columns: new[] { "target_type", "target_id" });

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_family_id",
                table: "refresh_tokens",
                column: "family_id");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_token_hash",
                table: "refresh_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_user_id_revoked_at",
                table: "refresh_tokens",
                columns: new[] { "user_id", "revoked_at" });

            migrationBuilder.CreateIndex(
                name: "ix_users_role_status",
                table: "users",
                columns: new[] { "role", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_users_email",
                table: "users",
                column: "email",
                unique: true);

            // The admin action log is append-only, like the ledger and audit tables (function from AppendOnlyGuards).
            migrationBuilder.Sql(
                """
                CREATE TRIGGER admin_actions_no_update_delete
                    BEFORE UPDATE OR DELETE ON admin_actions
                    FOR EACH ROW EXECUTE FUNCTION reject_append_only_mutation();
                CREATE TRIGGER admin_actions_no_truncate
                    BEFORE TRUNCATE ON admin_actions
                    FOR EACH STATEMENT EXECUTE FUNCTION reject_append_only_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS admin_actions_no_update_delete ON admin_actions;
                DROP TRIGGER IF EXISTS admin_actions_no_truncate ON admin_actions;
                """);

            migrationBuilder.DropTable(
                name: "admin_actions");

            migrationBuilder.DropTable(
                name: "refresh_tokens");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropCheckConstraint(
                name: "ck_wallets_frozen_has_reason",
                table: "wallets");

            migrationBuilder.DropColumn(
                name: "frozen_at",
                table: "wallets");

            migrationBuilder.DropColumn(
                name: "frozen_reason",
                table: "wallets");

            migrationBuilder.DropColumn(
                name: "status",
                table: "wallets");
        }
    }
}
