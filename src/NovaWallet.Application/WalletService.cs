using System.Globalization;
using Microsoft.Extensions.Logging;
using NovaWallet.Application.Abstractions;
using NovaWallet.Domain;

namespace NovaWallet.Application;

public sealed class WalletService(
    ILedgerStore store,
    IUserStore users,
    RequestGuard guard,
    TimeProvider timeProvider,
    ILogger<WalletService> logger)
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    public async Task<WalletResponse> CreateAsync(Actor actor, CreateWalletCommand command, CancellationToken ct)
    {
        // customerId (when supplied) is a user id and is accepted in any valid GUID format
        // (with or without dashes — every GUID the API shows elsewhere, e.g. /me, uses dashes).
        // Comparing and looking it up by value, not by raw string, means the caller's own id
        // is recognised regardless of which format they copied it in.
        Guid userId;
        if (command.CustomerId is null)
            userId = actor.UserId;
        else if (!Guid.TryParse(command.CustomerId, out userId))
            throw new RequestValidationException("customerId must be a valid user id.");

        if (!actor.IsAdmin && userId != actor.UserId)
            throw new ForbiddenException("Customers can only create a wallet for themselves.");

        // Wallets belong to registered users; the customer id is the user's id.
        var user = await users.FindByIdAsync(userId, ct)
                   ?? throw new RequestValidationException("customerId must be the id of a registered user.");

        var wallet = new Wallet(Guid.NewGuid(), user.SubjectId, timeProvider.GetLedgerNow());
        if (!await store.TryCreateWalletAsync(wallet, ct))
            throw new WalletAlreadyExistsException();

        logger.LogInformation("Wallet {WalletId} created", wallet.Id);
        return ToResponse(wallet);
    }

    public async Task<WalletResponse> GetAsync(Actor actor, Guid walletId, CancellationToken ct)
        => ToResponse(await GetAuthorizedAsync(actor, walletId, ct));

    public async Task<BalanceResponse> GetBalanceAsync(Actor actor, Guid walletId, CancellationToken ct)
    {
        var wallet = await GetAuthorizedAsync(actor, walletId, ct);
        return new BalanceResponse(wallet.Id, wallet.BalanceKobo, wallet.Currency, wallet.Balance.ToString(), wallet.UpdatedAt);
    }

    /// <summary>
    /// Simulates an inbound NIP credit, so only administrators (standing in for the settlement integration) may call it.
    /// The NIP session reference makes it idempotent: the same reference, wallet and amount returns the
    /// original receipt; the same reference with different details is rejected.
    /// </summary>
    public async Task<IdempotentResult<TransactionReceipt>> CreditAsync(
        Actor actor, Guid walletId, CreditCommand command, string? correlationId, CancellationToken ct)
    {
        if (!actor.IsAdmin)
            throw new ForbiddenException("Only an administrator (the NIP settlement integration) can credit wallets.");

        var amount = guard.Amount(command.AmountKobo);
        var reference = RequestGuard.Reference(command.Reference);
        var narration = RequestGuard.Narration(command.Narration);

        await using var tx = await store.BeginAsync(ct);
        var now = timeProvider.GetLedgerNow();

        var wallet = await store.LockWalletAsync(walletId, ct) ?? throw new WalletNotFoundException(walletId);

        var existing = await store.FindTransactionByReferenceAsync(reference, ct);
        if (existing is not null)
            return new IdempotentResult<TransactionReceipt>(await ReplayCreditAsync(existing, walletId, amount, ct), Replayed: true);

        var before = wallet.BalanceKobo;
        wallet.Credit(amount, now);

        var transaction = new LedgerTransaction(
            Guid.NewGuid(), TransactionType.Credit, null, wallet.Id, amount, reference, narration, now);
        store.Add(transaction);
        store.Add(new LedgerEntry(wallet.Id, transaction.Id, EntryDirection.Credit, amount, wallet.Balance, null, now));
        store.Add(new AuditRecord(wallet.Id, transaction.Id, AuditActions.Credit, amount.Kobo, before, wallet.BalanceKobo,
            actor.SubjectId, correlationId, now, await store.GetLatestAuditHashAsync(wallet.Id, ct)));

        try
        {
            await store.SaveChangesAsync(ct);
        }
        catch (UniqueConstraintException)
        {
            // The same reference was committed concurrently for a different wallet (a same-wallet duplicate
            // would have waited on the wallet lock and been caught by the lookup above).
            throw new DuplicateReferenceException(reference);
        }

        await tx.CommitAsync(ct);
        logger.LogInformation("Wallet {WalletId} credited {AmountKobo} kobo, transaction {TransactionId}",
            wallet.Id, amount.Kobo, transaction.Id);

        return new IdempotentResult<TransactionReceipt>(ToReceipt(transaction, wallet.BalanceKobo), Replayed: false);
    }

    public async Task<StatementPage> GetStatementAsync(
        Actor actor, Guid walletId, int? limit, string? cursor, CancellationToken ct)
    {
        var wallet = await GetAuthorizedAsync(actor, walletId, ct);

        var pageSize = limit ?? DefaultPageSize;
        if (pageSize is < 1 or > MaxPageSize)
            throw new RequestValidationException($"limit must be between 1 and {MaxPageSize}.");

        long? before = null;
        if (cursor is not null)
        {
            if (!long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
                throw new RequestValidationException("cursor is invalid.");
            before = parsed;
        }

        // Fetch one extra row to know whether there is a next page without a COUNT query.
        var entries = await store.GetEntriesAsync(wallet.Id, before, pageSize + 1, ct);
        var page = entries.Take(pageSize).Select(e => new StatementItem(
            e.Id,
            e.TransactionId,
            e.Transaction!.Type.ToString(),
            e.Direction.ToString(),
            e.AmountKobo,
            e.BalanceAfterKobo,
            e.CounterpartyWalletId,
            e.Transaction.Reference,
            e.Transaction.Narration,
            e.CreatedAt)).ToList();

        var nextCursor = entries.Count > pageSize
            ? page[^1].EntryId.ToString(CultureInfo.InvariantCulture)
            : null;

        return new StatementPage(wallet.Id, wallet.Currency, page, nextCursor);
    }

    public async Task<AuditTrailResponse> GetAuditTrailAsync(Actor actor, Guid walletId, CancellationToken ct)
    {
        if (!actor.IsAdmin)
            throw new ForbiddenException("Only administrators can read the audit trail.");
        _ = await store.FindWalletAsync(walletId, ct) ?? throw new WalletNotFoundException(walletId);

        var records = await store.GetAuditTrailAsync(walletId, ct);
        var broken = AuditChain.Verify(records);

        return new AuditTrailResponse(walletId, broken is null, broken, records.Select(r => new AuditItem(
            r.Id, r.TransactionId, r.Action, r.DeltaKobo, r.BalanceBeforeKobo, r.BalanceAfterKobo,
            r.Actor, r.CorrelationId, r.OccurredAt, r.PreviousHash, r.Hash)).ToList());
    }

    private async Task<Wallet> GetAuthorizedAsync(Actor actor, Guid walletId, CancellationToken ct)
    {
        var wallet = await store.FindWalletAsync(walletId, ct);
        // Report someone else's wallet as not found so ids can't be probed.
        if (wallet is null || (!actor.IsAdmin && !wallet.IsOwnedBy(actor.SubjectId)))
            throw new WalletNotFoundException(walletId);
        return wallet;
    }

    private async Task<TransactionReceipt> ReplayCreditAsync(
        LedgerTransaction existing, Guid walletId, Money amount, CancellationToken ct)
    {
        if (existing.Type != TransactionType.Credit
            || existing.DestinationWalletId != walletId
            || existing.AmountKobo != amount.Kobo)
        {
            throw new DuplicateReferenceException(existing.Reference);
        }

        var entry = await store.FindEntryAsync(existing.Id, walletId, ct)
                    ?? throw new InvalidOperationException($"Ledger entry missing for transaction {existing.Id}.");
        return ToReceipt(existing, entry.BalanceAfterKobo);
    }

    private static TransactionReceipt ToReceipt(LedgerTransaction t, long balanceAfterKobo) => new(
        t.Id, t.Type.ToString(), t.Reference, t.SourceWalletId, t.DestinationWalletId,
        t.AmountKobo, Money.Currency, balanceAfterKobo, t.Narration, t.CreatedAt);

    private static WalletResponse ToResponse(Wallet w) =>
        new(w.Id, w.CustomerId, w.BalanceKobo, w.Currency, w.Status.ToString(), w.FrozenReason, w.CreatedAt);
}
