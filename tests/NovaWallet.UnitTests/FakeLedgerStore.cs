using System.Reflection;
using NovaWallet.Application.Abstractions;
using NovaWallet.Domain;

namespace NovaWallet.UnitTests;

/// <summary>
/// Single-threaded in-memory <see cref="ILedgerStore"/>. Pending writes are buffered and applied on
/// SaveChanges, and wallet mutations are rolled back unless the scope commits, mimicking a transaction.
/// </summary>
internal sealed class FakeLedgerStore : ILedgerStore
{
    private readonly List<object> _pending = [];
    private readonly Dictionary<Guid, long> _balancesAtBegin = [];
    private readonly Dictionary<(string, string), IdempotencyRecord> _pendingKeys = [];

    public Dictionary<Guid, Wallet> Wallets { get; } = [];
    public List<LedgerTransaction> Transactions { get; } = [];
    public List<LedgerEntry> Entries { get; } = [];
    public List<AuditRecord> Audit { get; } = [];
    public List<OutboxMessage> Outbox { get; } = [];
    public Dictionary<(string, string), IdempotencyRecord> Keys { get; } = [];
    public List<Guid> LockOrder { get; } = [];
    public bool Committed { get; private set; }

    public Wallet Seed(string customerId, long balanceKobo, DateTimeOffset now)
    {
        var wallet = new Wallet(Guid.NewGuid(), customerId, now);
        if (balanceKobo > 0)
            wallet.Credit(Money.FromKobo(balanceKobo), now);
        Wallets[wallet.Id] = wallet;
        return wallet;
    }

    public Task<ILedgerTransactionScope> BeginAsync(CancellationToken ct)
    {
        Committed = false;
        LockOrder.Clear();
        _balancesAtBegin.Clear();
        foreach (var w in Wallets.Values)
            _balancesAtBegin[w.Id] = w.BalanceKobo;
        return Task.FromResult<ILedgerTransactionScope>(new Scope(this));
    }

    public Task<Wallet?> FindWalletAsync(Guid walletId, CancellationToken ct) =>
        Task.FromResult(Wallets.GetValueOrDefault(walletId));

    public Task<Guid?> FindWalletIdByCustomerAsync(string customerId, CancellationToken ct) =>
        Task.FromResult(Wallets.Values.FirstOrDefault(w => w.CustomerId == customerId)?.Id);

    public Task<bool> TryCreateWalletAsync(Wallet wallet, CancellationToken ct)
    {
        if (Wallets.Values.Any(w => w.CustomerId == wallet.CustomerId))
            return Task.FromResult(false);
        Wallets[wallet.Id] = wallet;
        return Task.FromResult(true);
    }

    public Task<Wallet?> LockWalletAsync(Guid walletId, CancellationToken ct)
    {
        LockOrder.Add(walletId);
        return Task.FromResult(Wallets.GetValueOrDefault(walletId));
    }

    public Task<IdempotencyClaim> ClaimIdempotencyKeyAsync(
        string scope, string key, string requestHash, DateTimeOffset now, CancellationToken ct)
    {
        if (Keys.TryGetValue((scope, key), out var existing))
            return Task.FromResult(new IdempotencyClaim(false, existing));
        _pendingKeys[(scope, key)] = new IdempotencyRecord(scope, key, requestHash, now);
        return Task.FromResult(new IdempotencyClaim(true, null));
    }

    public Task CompleteIdempotencyKeyAsync(string scope, string key, IdempotencyOutcome outcome, string? responseJson,
        string? errorCode, string? errorMessage, DateTimeOffset now, CancellationToken ct)
    {
        _pendingKeys[(scope, key)].Complete(outcome, responseJson, errorCode, errorMessage, now);
        return Task.CompletedTask;
    }

    public Task<long> GetOutboundTransferTotalKoboAsync(Guid walletId, DateTimeOffset sinceUtc, CancellationToken ct) =>
        Task.FromResult(Transactions
            .Where(t => t.SourceWalletId == walletId && t.Type == TransactionType.Transfer && t.CreatedAt >= sinceUtc)
            .Sum(t => t.AmountKobo));

    public Task<LedgerTransaction?> FindTransactionByReferenceAsync(string reference, CancellationToken ct) =>
        Task.FromResult(Transactions.SingleOrDefault(t => t.Reference == reference));

    public Task<LedgerEntry?> FindEntryAsync(Guid transactionId, Guid walletId, CancellationToken ct) =>
        Task.FromResult(Entries.FirstOrDefault(e => e.TransactionId == transactionId && e.WalletId == walletId));

    public Task<string> GetLatestAuditHashAsync(Guid walletId, CancellationToken ct) =>
        Task.FromResult(Audit.LastOrDefault(a => a.WalletId == walletId)?.Hash ?? AuditRecord.GenesisHash);

    public void Add(LedgerTransaction transaction) => _pending.Add(transaction);
    public void Add(LedgerEntry entry) => _pending.Add(entry);
    public void Add(AuditRecord record) => _pending.Add(record);
    public void Add(OutboxMessage message) => _pending.Add(message);

    public Task SaveChangesAsync(CancellationToken ct)
    {
        foreach (var item in _pending)
        {
            switch (item)
            {
                case LedgerTransaction t: Transactions.Add(t); break;
                case LedgerEntry e:
                    SetId(e, Entries.Count + 1);
                    Entries.Add(e);
                    break;
                case AuditRecord a:
                    SetId(a, Audit.Count + 1);
                    Audit.Add(a);
                    break;
                case OutboxMessage m: Outbox.Add(m); break;
            }
        }
        _pending.Clear();
        return Task.CompletedTask;
    }

    public void DiscardPendingChanges()
    {
        _pending.Clear();
        RestoreBalances();
    }

    public Task<IReadOnlyList<LedgerEntry>> GetEntriesAsync(Guid walletId, long? beforeEntryId, int take, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<LedgerEntry>>(Entries
            .Where(e => e.WalletId == walletId && (beforeEntryId == null || e.Id < beforeEntryId))
            .OrderByDescending(e => e.Id).Take(take).ToList());

    public Task<IReadOnlyList<AuditRecord>> GetAuditTrailAsync(Guid walletId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AuditRecord>>(Audit.Where(a => a.WalletId == walletId).ToList());

    public Task<IReadOnlyList<Wallet>> ListWalletsAsync(WalletStatus? status, Guid? afterId, int take, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Wallet>>(Wallets.Values
            .Where(w => status == null || w.Status == status)
            .Where(w => afterId == null || w.Id.CompareTo(afterId.Value) > 0)
            .OrderBy(w => w.Id).Take(take).ToList());

    private void RestoreBalances()
    {
        foreach (var (id, balance) in _balancesAtBegin)
        {
            var wallet = Wallets[id];
            var diff = balance - wallet.BalanceKobo;
            if (diff > 0) wallet.Credit(Money.FromKobo(diff), wallet.UpdatedAt);
            else if (diff < 0) wallet.Debit(Money.FromKobo(-diff), wallet.UpdatedAt);
        }
    }

    private static void SetId(object entity, long id) =>
        entity.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance)!.SetValue(entity, id);

    private sealed class Scope(FakeLedgerStore store) : ILedgerTransactionScope
    {
        private bool _committed;

        public Task CommitAsync(CancellationToken ct)
        {
            foreach (var (k, v) in store._pendingKeys)
                store.Keys[k] = v;
            store._pendingKeys.Clear();
            _committed = store.Committed = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (!_committed)
            {
                store._pendingKeys.Clear();
                store._pending.Clear();
                store.RestoreBalances();
            }
            return ValueTask.CompletedTask;
        }
    }
}
