using System.Text.Json;
using Microsoft.Extensions.Logging;
using NovaWallet.Application.Abstractions;
using NovaWallet.Domain;

namespace NovaWallet.Application;

public sealed class TransferService(
    ILedgerStore store,
    DailyLimitPolicy dailyLimit,
    RequestGuard guard,
    TimeProvider timeProvider,
    ILogger<TransferService> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Moves money from the caller's own wallet to another wallet in one database transaction. The source is
    /// resolved from the authenticated user, never from the request, so nobody can name someone else's wallet.
    /// Steps:
    /// <list type="number">
    /// <item>Claim the idempotency key (a concurrent duplicate blocks here until the first request commits).</item>
    /// <item>Lock both wallet rows in a fixed order (prevents A→B / B→A deadlocks).</item>
    /// <item>Check ownership, balance and the daily limit while holding the locks.</item>
    /// <item>Write balances, transaction, ledger entries, audit records, outbox event and the stored response.</item>
    /// </list>
    /// Business rejections are also stored against the key, so a replay returns the same error.
    /// </summary>
    public async Task<IdempotentResult<TransactionReceipt>> TransferAsync(
        Actor actor, string? idempotencyKey, TransferCommand command, string? correlationId, CancellationToken ct)
    {
        var key = RequestGuard.IdempotencyKey(idempotencyKey);
        var amount = guard.Amount(command.AmountKobo);
        var narration = RequestGuard.Narration(command.Narration);

        var sourceWalletId = await store.FindWalletIdByCustomerAsync(actor.SubjectId, ct)
                             ?? throw new WalletNotFoundException("You don't have a wallet yet. Create one first.");
        if (sourceWalletId == command.DestinationWalletId)
            throw new SameWalletTransferException();

        // The key is scoped to the caller, and the caller has one wallet, so the source needn't be hashed.
        var requestHash = RequestGuard.HashRequest("transfer", command.DestinationWalletId, amount.Kobo, narration);

        await using var tx = await store.BeginAsync(ct);
        var now = timeProvider.GetLedgerNow();

        var claim = await store.ClaimIdempotencyKeyAsync(actor.SubjectId, key, requestHash, now, ct);
        if (!claim.IsNew)
            return Replay(claim.Existing!, requestHash);

        try
        {
            var receipt = await ExecuteAsync(actor, sourceWalletId, command, amount, narration, correlationId, now, ct);

            await store.SaveChangesAsync(ct);
            await store.CompleteIdempotencyKeyAsync(actor.SubjectId, key, IdempotencyOutcome.Succeeded,
                JsonSerializer.Serialize(receipt, Json), null, null, now, ct);
            await tx.CommitAsync(ct);

            logger.LogInformation(
                "Transfer {TransactionId} of {AmountKobo} kobo from {SourceWalletId} to {DestinationWalletId} completed",
                receipt.TransactionId, amount.Kobo, sourceWalletId, command.DestinationWalletId);
            return new IdempotentResult<TransactionReceipt>(receipt, Replayed: false);
        }
        catch (DomainException ex)
        {
            // Nothing has been written except the key claim; record the rejection against it and commit,
            // which also releases the wallet locks.
            store.DiscardPendingChanges();
            await store.CompleteIdempotencyKeyAsync(actor.SubjectId, key, IdempotencyOutcome.Rejected,
                null, ex.Code, ex.Message, now, ct);
            await tx.CommitAsync(ct);

            logger.LogInformation("Transfer from {SourceWalletId} rejected: {ErrorCode}", sourceWalletId, ex.Code);
            throw;
        }
    }

    private async Task<TransactionReceipt> ExecuteAsync(
        Actor actor, Guid sourceWalletId, TransferCommand command, Money amount, string? narration, string? correlationId,
        DateTimeOffset now, CancellationToken ct)
    {
        // Always lock the lower id first so two opposing transfers can never wait on each other.
        var (firstId, secondId) = sourceWalletId.CompareTo(command.DestinationWalletId) < 0
            ? (sourceWalletId, command.DestinationWalletId)
            : (command.DestinationWalletId, sourceWalletId);
        var first = await store.LockWalletAsync(firstId, ct);
        var second = await store.LockWalletAsync(secondId, ct);

        var source = firstId == sourceWalletId ? first : second;
        var destination = firstId == sourceWalletId ? second : first;

        // Defence in depth: the source was looked up by owner, so this should never fire.
        if (source is null || !source.IsOwnedBy(actor.SubjectId))
            throw new WalletNotFoundException(sourceWalletId);
        if (destination is null)
            throw new WalletNotFoundException(command.DestinationWalletId);

        var spentToday = await store.GetOutboundTransferTotalKoboAsync(
            source.Id, DailyLimitPolicy.WindowStartUtc(now), ct);
        dailyLimit.EnsureWithinLimit(Money.FromKobo(spentToday), amount);

        var sourceBefore = source.BalanceKobo;
        var destinationBefore = destination.BalanceKobo;
        source.Debit(amount, now);
        destination.Credit(amount, now);

        var transactionId = Guid.NewGuid();
        var transaction = new LedgerTransaction(
            transactionId, TransactionType.Transfer, source.Id, destination.Id, amount,
            reference: $"TRF-{transactionId:N}", narration, now);
        store.Add(transaction);

        store.Add(new LedgerEntry(source.Id, transactionId, EntryDirection.Debit, amount, source.Balance, destination.Id, now));
        store.Add(new LedgerEntry(destination.Id, transactionId, EntryDirection.Credit, amount, destination.Balance, source.Id, now));

        store.Add(new AuditRecord(source.Id, transactionId, AuditActions.TransferDebit, -amount.Kobo,
            sourceBefore, source.BalanceKobo, actor.SubjectId, correlationId, now,
            await store.GetLatestAuditHashAsync(source.Id, ct)));
        store.Add(new AuditRecord(destination.Id, transactionId, AuditActions.TransferCredit, amount.Kobo,
            destinationBefore, destination.BalanceKobo, actor.SubjectId, correlationId, now,
            await store.GetLatestAuditHashAsync(destination.Id, ct)));

        var @event = new TransferCompletedEvent(Guid.NewGuid(), transactionId, transaction.Reference,
            source.Id, destination.Id, amount.Kobo, Money.Currency, now, correlationId);
        store.Add(new OutboxMessage(@event.EventId, TransferCompletedEvent.EventType,
            JsonSerializer.Serialize(@event, Json), now));

        return new TransactionReceipt(transactionId, nameof(TransactionType.Transfer), transaction.Reference,
            source.Id, destination.Id, amount.Kobo, Money.Currency, source.BalanceKobo, narration, now);
    }

    private static IdempotentResult<TransactionReceipt> Replay(IdempotencyRecord existing, string requestHash)
    {
        if (existing.RequestHash != requestHash)
            throw new IdempotencyKeyReusedException();

        return existing.Outcome switch
        {
            IdempotencyOutcome.Succeeded => new IdempotentResult<TransactionReceipt>(
                JsonSerializer.Deserialize<TransactionReceipt>(existing.ResponseJson!, Json)!, Replayed: true),
            IdempotencyOutcome.Rejected => throw new ReplayedRejectionException(existing.ErrorCode!, existing.ErrorMessage!),
            // A committed key is always completed in the same transaction, so this indicates a bug.
            _ => throw new InvalidOperationException("Idempotency record has no outcome."),
        };
    }
}
