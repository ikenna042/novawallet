using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NovaWallet.Domain;

namespace NovaWallet.Infrastructure.Persistence;

public sealed class LedgerDbContext(DbContextOptions<LedgerDbContext> options) : DbContext(options)
{
    public const string TransactionReferenceIndex = "ux_ledger_transactions_reference";
    public const string WalletCustomerIndex = "ux_wallets_customer_id";
    public const string UserEmailIndex = "ux_users_email";

    public DbSet<Wallet> Wallets => Set<Wallet>();
    public DbSet<LedgerTransaction> Transactions => Set<LedgerTransaction>();
    public DbSet<LedgerEntry> Entries => Set<LedgerEntry>();
    public DbSet<AuditRecord> AuditLog => Set<AuditRecord>();
    public DbSet<IdempotencyRecord> IdempotencyKeys => Set<IdempotencyRecord>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<AdminAction> AdminActions => Set<AdminAction>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        ConfigureWallet(b.Entity<Wallet>());
        ConfigureTransaction(b.Entity<LedgerTransaction>());
        ConfigureEntry(b.Entity<LedgerEntry>());
        ConfigureAudit(b.Entity<AuditRecord>());
        ConfigureIdempotency(b.Entity<IdempotencyRecord>());
        ConfigureOutbox(b.Entity<OutboxMessage>());
        ConfigureUser(b.Entity<User>());
        ConfigureRefreshToken(b.Entity<RefreshToken>());
        ConfigureAdminAction(b.Entity<AdminAction>());
    }

    private static void ConfigureWallet(EntityTypeBuilder<Wallet> e)
    {
        e.ToTable("wallets", t =>
        {
            // Last line of defence: even a bug in the application cannot persist a negative balance.
            t.HasCheckConstraint("ck_wallets_balance_non_negative", "balance_kobo >= 0");
            t.HasCheckConstraint("ck_wallets_currency_ngn", "currency = 'NGN'");
            t.HasCheckConstraint("ck_wallets_frozen_has_reason", "status <> 'Frozen' OR frozen_reason IS NOT NULL");
        });
        e.HasKey(w => w.Id);
        e.Property(w => w.CustomerId).HasMaxLength(64).IsRequired();
        e.Property(w => w.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        e.HasIndex(w => w.CustomerId).IsUnique().HasDatabaseName(WalletCustomerIndex);
        e.Property(w => w.Status).HasConversion<string>().HasMaxLength(16);
        e.Property(w => w.FrozenReason).HasMaxLength(200);
        e.Ignore(w => w.Balance);
    }

    private static void ConfigureTransaction(EntityTypeBuilder<LedgerTransaction> e)
    {
        e.ToTable("ledger_transactions", t =>
        {
            t.HasCheckConstraint("ck_ledger_transactions_amount_positive", "amount_kobo > 0");
            t.HasCheckConstraint("ck_ledger_transactions_transfer_shape",
                "type <> 'Transfer' OR (source_wallet_id IS NOT NULL AND source_wallet_id <> destination_wallet_id)");
        });
        e.HasKey(t => t.Id);
        e.Property(t => t.Type).HasConversion<string>().HasMaxLength(16);
        e.Property(t => t.Reference).HasMaxLength(64).IsRequired();
        e.Property(t => t.Narration).HasMaxLength(100);
        e.HasIndex(t => t.Reference).IsUnique().HasDatabaseName(TransactionReferenceIndex);
        // Serves the daily-limit query: outbound transfers from a wallet since WAT midnight.
        e.HasIndex(t => new { t.SourceWalletId, t.CreatedAt });
        e.HasOne<Wallet>().WithMany().HasForeignKey(t => t.SourceWalletId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<Wallet>().WithMany().HasForeignKey(t => t.DestinationWalletId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureEntry(EntityTypeBuilder<LedgerEntry> e)
    {
        e.ToTable("ledger_entries", t =>
        {
            t.HasCheckConstraint("ck_ledger_entries_amount_positive", "amount_kobo > 0");
            t.HasCheckConstraint("ck_ledger_entries_balance_non_negative", "balance_after_kobo >= 0");
        });
        e.HasKey(x => x.Id);
        e.Property(x => x.Id).UseIdentityAlwaysColumn();
        e.Property(x => x.Direction).HasConversion<string>().HasMaxLength(8);
        // Statement query: newest first for one wallet, keyset-paginated on id.
        e.HasIndex(x => new { x.WalletId, x.Id }).IsDescending(false, true);
        e.HasIndex(x => new { x.TransactionId, x.WalletId });
        e.HasOne(x => x.Transaction).WithMany().HasForeignKey(x => x.TransactionId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<Wallet>().WithMany().HasForeignKey(x => x.WalletId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureAudit(EntityTypeBuilder<AuditRecord> e)
    {
        e.ToTable("audit_log", t =>
        {
            t.HasCheckConstraint("ck_audit_log_balances_add_up", "balance_before_kobo + delta_kobo = balance_after_kobo");
            t.HasCheckConstraint("ck_audit_log_balance_non_negative", "balance_after_kobo >= 0");
        });
        e.HasKey(a => a.Id);
        e.Property(a => a.Id).UseIdentityAlwaysColumn();
        e.Property(a => a.Action).HasMaxLength(32).IsRequired();
        e.Property(a => a.Actor).HasMaxLength(64).IsRequired();
        e.Property(a => a.CorrelationId).HasMaxLength(128);
        e.Property(a => a.PreviousHash).HasMaxLength(64).IsFixedLength().IsRequired();
        e.Property(a => a.Hash).HasMaxLength(64).IsFixedLength().IsRequired();
        e.HasIndex(a => new { a.WalletId, a.Id });
        e.HasIndex(a => a.Hash).IsUnique();
        // Deliberately no foreign keys: the audit trail must stand on its own.
    }

    private static void ConfigureIdempotency(EntityTypeBuilder<IdempotencyRecord> e)
    {
        e.ToTable("idempotency_keys");
        e.HasKey(k => new { k.Scope, k.Key });
        e.Property(k => k.Scope).HasMaxLength(64);
        e.Property(k => k.Key).HasMaxLength(64);
        e.Property(k => k.RequestHash).HasMaxLength(64).IsFixedLength().IsRequired();
        e.Property(k => k.Outcome).HasConversion<string>().HasMaxLength(16);
        e.Property(k => k.ResponseJson).HasColumnType("jsonb");
        e.Property(k => k.ErrorCode).HasMaxLength(64);
        e.Property(k => k.ErrorMessage).HasMaxLength(500);
        e.HasIndex(k => k.CreatedAt); // for a future expiry job
    }

    private static void ConfigureOutbox(EntityTypeBuilder<OutboxMessage> e)
    {
        e.ToTable("outbox_messages");
        e.HasKey(m => m.Id);
        e.Property(m => m.Type).HasMaxLength(128).IsRequired();
        e.Property(m => m.Payload).HasColumnType("jsonb").IsRequired();
        e.Property(m => m.LastError).HasMaxLength(2000);
        e.HasIndex(m => m.OccurredAt).HasFilter("processed_at IS NULL").HasDatabaseName("ix_outbox_messages_pending");
    }

    private static void ConfigureUser(EntityTypeBuilder<User> e)
    {
        e.ToTable("users", t =>
        {
            t.HasCheckConstraint("ck_users_email_normalized", "email = lower(email)");
            t.HasCheckConstraint("ck_users_token_version_positive", "token_version >= 1");
        });
        e.HasKey(u => u.Id);
        e.Property(u => u.Email).HasMaxLength(254).IsRequired();
        e.Property(u => u.FullName).HasMaxLength(100);
        e.Property(u => u.PasswordHash).HasMaxLength(256).IsRequired();
        e.Property(u => u.Role).HasConversion<string>().HasMaxLength(16);
        e.Property(u => u.Status).HasConversion<string>().HasMaxLength(16);
        e.Property(u => u.DisabledReason).HasMaxLength(200);
        e.HasIndex(u => u.Email).IsUnique().HasDatabaseName(UserEmailIndex);
        e.HasIndex(u => new { u.Role, u.Status });
        e.Ignore(u => u.SubjectId);
        e.Ignore(u => u.IsActive);
        e.Ignore(u => u.IsAdmin);
    }

    private static void ConfigureRefreshToken(EntityTypeBuilder<RefreshToken> e)
    {
        e.ToTable("refresh_tokens");
        e.HasKey(t => t.Id);
        e.Property(t => t.TokenHash).HasMaxLength(64).IsFixedLength().IsRequired();
        e.HasIndex(t => t.TokenHash).IsUnique();
        e.HasIndex(t => t.FamilyId);
        e.HasIndex(t => new { t.UserId, t.RevokedAt });
        e.HasOne<User>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureAdminAction(EntityTypeBuilder<AdminAction> e)
    {
        e.ToTable("admin_actions");
        e.HasKey(a => a.Id);
        e.Property(a => a.Id).UseIdentityAlwaysColumn();
        e.Property(a => a.ActorId).HasMaxLength(64).IsRequired();
        e.Property(a => a.Action).HasMaxLength(32).IsRequired();
        e.Property(a => a.TargetType).HasMaxLength(16).IsRequired();
        e.Property(a => a.TargetId).HasMaxLength(64).IsRequired();
        e.Property(a => a.Detail).HasMaxLength(200);
        e.Property(a => a.CorrelationId).HasMaxLength(128);
        e.HasIndex(a => new { a.TargetType, a.TargetId });
        // No foreign keys, like the audit log: the record must outlive whatever it refers to.
    }
}
