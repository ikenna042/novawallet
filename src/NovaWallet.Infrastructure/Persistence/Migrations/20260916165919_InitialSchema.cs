using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NovaWallet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_log",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    wallet_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    delta_kobo = table.Column<long>(type: "bigint", nullable: false),
                    balance_before_kobo = table.Column<long>(type: "bigint", nullable: false),
                    balance_after_kobo = table.Column<long>(type: "bigint", nullable: false),
                    actor = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    previous_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_log", x => x.id);
                    table.CheckConstraint("ck_audit_log_balance_non_negative", "balance_after_kobo >= 0");
                    table.CheckConstraint("ck_audit_log_balances_add_up", "balance_before_kobo + delta_kobo = balance_after_kobo");
                });

            migrationBuilder.CreateTable(
                name: "idempotency_keys",
                columns: table => new
                {
                    scope = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    request_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    response_json = table.Column<string>(type: "jsonb", nullable: true),
                    error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    error_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_keys", x => new { x.scope, x.key });
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "wallets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    balance_kobo = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_wallets", x => x.id);
                    table.CheckConstraint("ck_wallets_balance_non_negative", "balance_kobo >= 0");
                    table.CheckConstraint("ck_wallets_currency_ngn", "currency = 'NGN'");
                });

            migrationBuilder.CreateTable(
                name: "ledger_transactions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    source_wallet_id = table.Column<Guid>(type: "uuid", nullable: true),
                    destination_wallet_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount_kobo = table.Column<long>(type: "bigint", nullable: false),
                    reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    narration = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ledger_transactions", x => x.id);
                    table.CheckConstraint("ck_ledger_transactions_amount_positive", "amount_kobo > 0");
                    table.CheckConstraint("ck_ledger_transactions_transfer_shape", "type <> 'Transfer' OR (source_wallet_id IS NOT NULL AND source_wallet_id <> destination_wallet_id)");
                    table.ForeignKey(
                        name: "fk_ledger_transactions_wallets_destination_wallet_id",
                        column: x => x.destination_wallet_id,
                        principalTable: "wallets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ledger_transactions_wallets_source_wallet_id",
                        column: x => x.source_wallet_id,
                        principalTable: "wallets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ledger_entries",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    wallet_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    direction = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    amount_kobo = table.Column<long>(type: "bigint", nullable: false),
                    balance_after_kobo = table.Column<long>(type: "bigint", nullable: false),
                    counterparty_wallet_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ledger_entries", x => x.id);
                    table.CheckConstraint("ck_ledger_entries_amount_positive", "amount_kobo > 0");
                    table.CheckConstraint("ck_ledger_entries_balance_non_negative", "balance_after_kobo >= 0");
                    table.ForeignKey(
                        name: "fk_ledger_entries_ledger_transactions_transaction_id",
                        column: x => x.transaction_id,
                        principalTable: "ledger_transactions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ledger_entries_wallets_wallet_id",
                        column: x => x.wallet_id,
                        principalTable: "wallets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_hash",
                table: "audit_log",
                column: "hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_wallet_id_id",
                table: "audit_log",
                columns: new[] { "wallet_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_keys_created_at",
                table: "idempotency_keys",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_ledger_entries_transaction_id_wallet_id",
                table: "ledger_entries",
                columns: new[] { "transaction_id", "wallet_id" });

            migrationBuilder.CreateIndex(
                name: "ix_ledger_entries_wallet_id_id",
                table: "ledger_entries",
                columns: new[] { "wallet_id", "id" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_ledger_transactions_destination_wallet_id",
                table: "ledger_transactions",
                column: "destination_wallet_id");

            migrationBuilder.CreateIndex(
                name: "ix_ledger_transactions_source_wallet_id_created_at",
                table: "ledger_transactions",
                columns: new[] { "source_wallet_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_ledger_transactions_reference",
                table: "ledger_transactions",
                column: "reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_pending",
                table: "outbox_messages",
                column: "occurred_at",
                filter: "processed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_wallets_customer_id",
                table: "wallets",
                column: "customer_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_log");

            migrationBuilder.DropTable(
                name: "idempotency_keys");

            migrationBuilder.DropTable(
                name: "ledger_entries");

            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "ledger_transactions");

            migrationBuilder.DropTable(
                name: "wallets");
        }
    }
}
