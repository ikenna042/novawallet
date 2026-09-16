using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NovaWallet.Application;
using NovaWallet.Domain;

namespace NovaWallet.UnitTests;

/// <summary>
/// Orchestration rules of the transfer, checked against an in-memory store. Real locking and concurrency
/// are covered by the integration tests against PostgreSQL.
/// </summary>
public class TransferServiceTests
{
    private readonly FakeLedgerStore _store = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 16, 10, 0, 0, TimeSpan.Zero));
    private readonly TransferService _service;
    private readonly Wallet _alice;
    private readonly Wallet _bob;

    public TransferServiceTests()
    {
        var options = Options.Create(new LedgerOptions { DailyTransferLimitKobo = 500_000_00 });
        _service = new TransferService(_store, new DailyLimitPolicy(options), new RequestGuard(options), _clock,
            NullLogger<TransferService>.Instance);

        _alice = _store.Seed("alice", 1_000_000_00, _clock.GetUtcNow());
        _bob = _store.Seed("bob", 0, _clock.GetUtcNow());
    }

    private static Actor Alice => new("alice", IsAdmin: false);

    private Task<IdempotentResult<TransactionReceipt>> Transfer(long amount, string key, string? narration = null, Actor? actor = null) =>
        _service.TransferAsync(actor ?? Alice, key, new TransferCommand(_alice.Id, _bob.Id, amount, narration), "corr-1",
            CancellationToken.None);

    [Fact]
    public async Task Successful_transfer_writes_balanced_double_entry_audit_and_outbox()
    {
        var result = await Transfer(250_00, "key-00000001");

        Assert.False(result.Replayed);
        Assert.Equal(1_000_000_00 - 250_00, result.Value.BalanceAfterKobo);
        Assert.Equal(1_000_000_00 - 250_00, _store.Wallets[_alice.Id].BalanceKobo);
        Assert.Equal(250_00, _store.Wallets[_bob.Id].BalanceKobo);

        Assert.Equal(2, _store.Entries.Count);
        Assert.Equal(0, _store.Entries.Sum(e => e.Direction == EntryDirection.Debit ? -e.AmountKobo : e.AmountKobo));
        Assert.Equal(2, _store.Audit.Count);
        Assert.All(_store.Audit, a => Assert.Equal("corr-1", a.CorrelationId));
        Assert.Single(_store.Outbox);
        Assert.Equal(TransferCompletedEvent.EventType, _store.Outbox[0].Type);
        Assert.True(_store.Committed);
    }

    [Fact]
    public async Task Wallets_are_locked_in_ascending_id_order_regardless_of_direction()
    {
        await Transfer(1_00, "key-00000001");
        var expected = new[] { _alice.Id, _bob.Id }.Order().ToArray();
        Assert.Equal(expected, _store.LockOrder);
    }

    [Fact]
    public async Task Replaying_a_key_returns_the_original_receipt_without_moving_money_again()
    {
        var first = await Transfer(250_00, "key-00000001");
        var second = await Transfer(250_00, "key-00000001");

        Assert.True(second.Replayed);
        Assert.Equal(first.Value, second.Value);
        Assert.Equal(250_00, _store.Wallets[_bob.Id].BalanceKobo);
        Assert.Single(_store.Transactions);
    }

    [Fact]
    public async Task Reusing_a_key_with_a_different_amount_is_rejected()
    {
        await Transfer(250_00, "key-00000001");
        await Assert.ThrowsAsync<IdempotencyKeyReusedException>(() => Transfer(999_00, "key-00000001"));
        Assert.Equal(250_00, _store.Wallets[_bob.Id].BalanceKobo);
    }

    [Fact]
    public async Task A_rejected_transfer_is_remembered_and_replays_the_same_error()
    {
        var bob = new Actor("bob", IsAdmin: false);
        var fromEmptyWallet = new TransferCommand(_bob.Id, _alice.Id, 1_00, null);
        Task<IdempotentResult<TransactionReceipt>> Attempt() =>
            _service.TransferAsync(bob, "key-00000002", fromEmptyWallet, null, CancellationToken.None);

        await Assert.ThrowsAsync<InsufficientFundsException>(Attempt);
        Assert.True(_store.Committed); // the rejection was stored

        var replay = await Assert.ThrowsAsync<ReplayedRejectionException>(Attempt);
        Assert.Equal(ErrorCodes.InsufficientFunds, replay.Code);
        Assert.Empty(_store.Transactions);
    }

    [Fact]
    public async Task Customer_cannot_spend_from_someone_elses_wallet()
    {
        await Assert.ThrowsAsync<WalletNotFoundException>(() =>
            Transfer(1_00, "key-00000003", actor: new Actor("mallory", IsAdmin: false)));
        Assert.Equal(0, _store.Wallets[_bob.Id].BalanceKobo);
    }

    [Fact]
    public async Task Daily_limit_counts_earlier_transfers_and_resets_at_wat_midnight()
    {
        // 10:00 UTC = 11:00 WAT
        await Transfer(300_000_00, "key-00000010");
        await Transfer(200_000_00, "key-00000011"); // exactly at the ₦500,000 limit
        await Assert.ThrowsAsync<DailyLimitExceededException>(() => Transfer(1, "key-00000012"));

        // 22:59:59 UTC = 23:59:59 WAT, still the same day
        _clock.SetUtcNow(new DateTimeOffset(2026, 9, 16, 22, 59, 59, TimeSpan.Zero));
        await Assert.ThrowsAsync<DailyLimitExceededException>(() => Transfer(1, "key-00000013"));

        // 23:00 UTC = 00:00 WAT the next day
        _clock.SetUtcNow(new DateTimeOffset(2026, 9, 16, 23, 0, 0, TimeSpan.Zero));
        var result = await Transfer(1, "key-00000014");
        Assert.False(result.Replayed);
    }

    [Fact]
    public async Task Transfer_to_the_same_wallet_is_rejected_before_touching_the_store()
    {
        await Assert.ThrowsAsync<SameWalletTransferException>(() => _service.TransferAsync(
            Alice, "key-00000020", new TransferCommand(_alice.Id, _alice.Id, 1_00, null), null, CancellationToken.None));
        Assert.Empty(_store.LockOrder);
    }

    [Fact]
    public async Task Missing_idempotency_key_is_rejected() =>
        await Assert.ThrowsAsync<RequestValidationException>(() => Transfer(1_00, key: ""));
}
