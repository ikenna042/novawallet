namespace NovaWallet.Application;

/// <summary>The authenticated caller, derived from JWT claims by the API layer.</summary>
public sealed record Actor(string SubjectId, bool IsOperator);

public sealed record CreateWalletCommand(string? CustomerId);

public sealed record CreditCommand(long AmountKobo, string? Reference, string? Narration);

public sealed record TransferCommand(Guid SourceWalletId, Guid DestinationWalletId, long AmountKobo, string? Narration);

public sealed record WalletResponse(Guid WalletId, string CustomerId, long BalanceKobo, string Currency, DateTimeOffset CreatedAt);

public sealed record BalanceResponse(Guid WalletId, long BalanceKobo, string Currency, string BalanceDisplay, DateTimeOffset UpdatedAt);

/// <summary>
/// Result of a credit or transfer. <see cref="BalanceAfterKobo"/> is the balance of the caller's side only
/// (the source wallet for a transfer, the credited wallet for a credit) so a sender never learns the
/// recipient's balance.
/// </summary>
public sealed record TransactionReceipt(
    Guid TransactionId,
    string Type,
    string Reference,
    Guid? SourceWalletId,
    Guid DestinationWalletId,
    long AmountKobo,
    string Currency,
    long BalanceAfterKobo,
    string? Narration,
    DateTimeOffset CreatedAt);

public sealed record IdempotentResult<T>(T Value, bool Replayed);

public sealed record StatementItem(
    long EntryId,
    Guid TransactionId,
    string Type,
    string Direction,
    long AmountKobo,
    long BalanceAfterKobo,
    Guid? CounterpartyWalletId,
    string Reference,
    string? Narration,
    DateTimeOffset CreatedAt);

public sealed record StatementPage(Guid WalletId, string Currency, IReadOnlyList<StatementItem> Items, string? NextCursor);

public sealed record AuditItem(
    long Id,
    Guid TransactionId,
    string Action,
    long DeltaKobo,
    long BalanceBeforeKobo,
    long BalanceAfterKobo,
    string Actor,
    string? CorrelationId,
    DateTimeOffset OccurredAt,
    string PreviousHash,
    string Hash);

public sealed record AuditTrailResponse(Guid WalletId, bool ChainIntact, long? FirstBrokenRecordId, IReadOnlyList<AuditItem> Records);

public sealed record TransferCompletedEvent(
    Guid EventId,
    Guid TransactionId,
    string Reference,
    Guid SourceWalletId,
    Guid DestinationWalletId,
    long AmountKobo,
    string Currency,
    DateTimeOffset OccurredAt,
    string? CorrelationId)
{
    public const string EventType = "wallet.transfer.completed.v1";
}
