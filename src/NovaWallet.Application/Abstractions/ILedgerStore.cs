using NovaWallet.Domain;

namespace NovaWallet.Application.Abstractions;

/// <summary>
/// Persistence port for the ledger. The implementation must run every call made between
/// <see cref="BeginAsync"/> and <see cref="ILedgerTransactionScope.CommitAsync"/> in one database transaction.
/// </summary>
public interface ILedgerStore
{
    Task<ILedgerTransactionScope> BeginAsync(CancellationToken ct);

    /// <summary>Read-only lookup (no lock). Never use the result to decide a balance mutation.</summary>
    Task<Wallet?> FindWalletAsync(Guid walletId, CancellationToken ct);

    /// <summary>The id of the customer's wallet (a customer has at most one), or null.</summary>
    Task<Guid?> FindWalletIdByCustomerAsync(string customerId, CancellationToken ct);

    /// <summary>Returns false if the customer already has a wallet.</summary>
    Task<bool> TryCreateWalletAsync(Wallet wallet, CancellationToken ct);

    /// <summary>
    /// Loads the wallet with a row-level write lock (SELECT ... FOR UPDATE) held until the transaction ends,
    /// so its balance cannot change underneath the caller. Callers locking more than one wallet must lock
    /// them in a consistent order to avoid deadlocks.
    /// </summary>
    Task<Wallet?> LockWalletAsync(Guid walletId, CancellationToken ct);

    /// <summary>
    /// Atomically inserts the key if it is new. If another in-flight transaction holds the same key,
    /// this blocks until that transaction finishes and then returns its committed record.
    /// </summary>
    Task<IdempotencyClaim> ClaimIdempotencyKeyAsync(
        string scope, string key, string requestHash, DateTimeOffset now, CancellationToken ct);

    Task CompleteIdempotencyKeyAsync(
        string scope, string key, IdempotencyOutcome outcome, string? responseJson,
        string? errorCode, string? errorMessage, DateTimeOffset now, CancellationToken ct);

    /// <summary>Sum of outbound transfers from the wallet created at or after <paramref name="sinceUtc"/>.</summary>
    Task<long> GetOutboundTransferTotalKoboAsync(Guid walletId, DateTimeOffset sinceUtc, CancellationToken ct);

    Task<LedgerTransaction?> FindTransactionByReferenceAsync(string reference, CancellationToken ct);

    Task<LedgerEntry?> FindEntryAsync(Guid transactionId, Guid walletId, CancellationToken ct);

    /// <summary>Hash of the wallet's most recent audit record, or the genesis hash if there is none.</summary>
    Task<string> GetLatestAuditHashAsync(Guid walletId, CancellationToken ct);

    void Add(LedgerTransaction transaction);
    void Add(LedgerEntry entry);
    void Add(AuditRecord record);
    void Add(OutboxMessage message);

    /// <exception cref="UniqueConstraintException">A unique index was violated.</exception>
    Task SaveChangesAsync(CancellationToken ct);

    /// <summary>Drops tracked but unsaved changes (used when a business rule rejects a request mid-way).</summary>
    void DiscardPendingChanges();

    /// <summary>Newest first. Entries include their transaction.</summary>
    Task<IReadOnlyList<LedgerEntry>> GetEntriesAsync(Guid walletId, long? beforeEntryId, int take, CancellationToken ct);

    /// <summary>Oldest first.</summary>
    Task<IReadOnlyList<AuditRecord>> GetAuditTrailAsync(Guid walletId, CancellationToken ct);
}

public interface ILedgerTransactionScope : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct);
}

public sealed record IdempotencyClaim(bool IsNew, IdempotencyRecord? Existing);

public sealed class UniqueConstraintException(string constraintName, Exception inner)
    : Exception($"Unique constraint '{constraintName}' was violated.", inner)
{
    public string ConstraintName { get; } = constraintName;
}
