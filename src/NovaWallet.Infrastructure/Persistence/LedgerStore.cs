using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using NovaWallet.Application.Abstractions;
using NovaWallet.Domain;

namespace NovaWallet.Infrastructure.Persistence;

public sealed class LedgerStore(LedgerDbContext db) : ILedgerStore
{
    public Task<ILedgerTransactionScope> BeginAsync(CancellationToken ct) => DbTransactionScope.BeginAsync(db, ct);

    public Task<Wallet?> FindWalletAsync(Guid walletId, CancellationToken ct) =>
        db.Wallets.AsNoTracking().SingleOrDefaultAsync(w => w.Id == walletId, ct);

    // Reading this before taking the lock is safe: a wallet never changes owner.
    public Task<Guid?> FindWalletIdByCustomerAsync(string customerId, CancellationToken ct) =>
        db.Wallets.AsNoTracking()
            .Where(w => w.CustomerId == customerId)
            .Select(w => (Guid?)w.Id)
            .SingleOrDefaultAsync(ct);

    public async Task<bool> TryCreateWalletAsync(Wallet wallet, CancellationToken ct)
    {
        db.Wallets.Add(wallet);
        try
        {
            await SaveChangesAsync(ct);
            return true;
        }
        catch (UniqueConstraintException ex) when (ex.ConstraintName == LedgerDbContext.WalletCustomerIndex)
        {
            db.Entry(wallet).State = EntityState.Detached;
            return false;
        }
    }

    public async Task<Wallet?> LockWalletAsync(Guid walletId, CancellationToken ct)
    {
        // EF returns an already-tracked instance as-is (with its old values) instead of the freshly locked
        // row, which would silently defeat the lock. Refuse rather than risk deciding on a stale balance.
        if (db.ChangeTracker.Entries<Wallet>().Any(e => e.Entity.Id == walletId))
            throw new InvalidOperationException($"Wallet {walletId} was loaded before it was locked.");

        // ToListAsync (not SingleOrDefaultAsync) so EF doesn't wrap the FOR UPDATE query in a subquery.
        var rows = await db.Wallets
            .FromSql($"SELECT * FROM wallets WHERE id = {walletId} FOR UPDATE")
            .ToListAsync(ct);
        return rows.SingleOrDefault();
    }

    public async Task<IdempotencyClaim> ClaimIdempotencyKeyAsync(
        string scope, string key, string requestHash, DateTimeOffset now, CancellationToken ct)
    {
        // If another transaction has inserted the same key but not yet committed, this INSERT waits on the
        // primary-key index. When that transaction commits we get 0 rows (and read its result below);
        // if it rolls back, our insert goes ahead and we own the key.
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO idempotency_keys (scope, key, request_hash, created_at)
             VALUES ({scope}, {key}, {requestHash}, {now})
             ON CONFLICT (scope, key) DO NOTHING
             """, ct);

        if (inserted == 1)
            return new IdempotencyClaim(IsNew: true, Existing: null);

        // READ COMMITTED takes a new snapshot per statement, so this sees the row that just committed.
        var existing = await db.IdempotencyKeys.AsNoTracking()
            .SingleAsync(k => k.Scope == scope && k.Key == key, ct);
        return new IdempotencyClaim(IsNew: false, existing);
    }

    public Task CompleteIdempotencyKeyAsync(
        string scope, string key, IdempotencyOutcome outcome, string? responseJson,
        string? errorCode, string? errorMessage, DateTimeOffset now, CancellationToken ct)
    {
        return db.Database.ExecuteSqlRawAsync(
            """
            UPDATE idempotency_keys
            SET outcome = @outcome, response_json = @response, error_code = @code,
                error_message = @message, completed_at = @now
            WHERE scope = @scope AND key = @key
            """,
            [
                Param("outcome", outcome.ToString(), NpgsqlDbType.Text),
                Param("response", responseJson, NpgsqlDbType.Jsonb),
                Param("code", errorCode, NpgsqlDbType.Text),
                Param("message", Truncate(errorMessage, 500), NpgsqlDbType.Text),
                Param("now", now, NpgsqlDbType.TimestampTz),
                Param("scope", scope, NpgsqlDbType.Text),
                Param("key", key, NpgsqlDbType.Text),
            ],
            ct);
    }

    public Task<long> GetOutboundTransferTotalKoboAsync(Guid walletId, DateTimeOffset sinceUtc, CancellationToken ct) =>
        db.Transactions
            .Where(t => t.SourceWalletId == walletId && t.Type == TransactionType.Transfer && t.CreatedAt >= sinceUtc)
            .SumAsync(t => t.AmountKobo, ct);

    public Task<LedgerTransaction?> FindTransactionByReferenceAsync(string reference, CancellationToken ct) =>
        db.Transactions.AsNoTracking().SingleOrDefaultAsync(t => t.Reference == reference, ct);

    public Task<LedgerEntry?> FindEntryAsync(Guid transactionId, Guid walletId, CancellationToken ct) =>
        db.Entries.AsNoTracking().FirstOrDefaultAsync(e => e.TransactionId == transactionId && e.WalletId == walletId, ct);

    public async Task<string> GetLatestAuditHashAsync(Guid walletId, CancellationToken ct) =>
        await db.AuditLog
            .Where(a => a.WalletId == walletId)
            .OrderByDescending(a => a.Id)
            .Select(a => a.Hash)
            .FirstOrDefaultAsync(ct)
        ?? AuditRecord.GenesisHash;

    public void Add(LedgerTransaction transaction) => db.Transactions.Add(transaction);
    public void Add(LedgerEntry entry) => db.Entries.Add(entry);
    public void Add(AuditRecord record) => db.AuditLog.Add(record);
    public void Add(OutboxMessage message) => db.Outbox.Add(message);

    public async Task SaveChangesAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            throw new UniqueConstraintException(pg.ConstraintName ?? "unknown", ex);
        }
    }

    public void DiscardPendingChanges() => db.ChangeTracker.Clear();

    public async Task<IReadOnlyList<LedgerEntry>> GetEntriesAsync(
        Guid walletId, long? beforeEntryId, int take, CancellationToken ct)
    {
        var query = db.Entries.AsNoTracking().Include(e => e.Transaction).Where(e => e.WalletId == walletId);
        if (beforeEntryId is { } before)
            query = query.Where(e => e.Id < before);
        return await query.OrderByDescending(e => e.Id).Take(take).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<AuditRecord>> GetAuditTrailAsync(Guid walletId, CancellationToken ct) =>
        await db.AuditLog.AsNoTracking().Where(a => a.WalletId == walletId).OrderBy(a => a.Id).ToListAsync(ct);

    private static NpgsqlParameter Param(string name, object? value, NpgsqlDbType type) =>
        new(name, type) { Value = value ?? DBNull.Value };

    private static string? Truncate(string? value, int max) =>
        value is { Length: var length } && length > max ? value[..max] : value;
}
