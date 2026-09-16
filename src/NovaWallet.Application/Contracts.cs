namespace NovaWallet.Application;

/// <summary>The authenticated caller, derived from JWT claims by the API layer.</summary>
public sealed record Actor(string SubjectId, bool IsAdmin)
{
    public Guid UserId => Guid.ParseExact(SubjectId, "N");
}

public sealed record CreateWalletCommand(string? CustomerId);

public sealed record CreditCommand(long AmountKobo, string? Reference, string? Narration);

/// <summary>A transfer from the caller's own wallet; the source is never taken from the request.</summary>
public sealed record TransferCommand(Guid DestinationWalletId, long AmountKobo, string? Narration);

public sealed record WalletResponse(
    Guid WalletId, string CustomerId, long BalanceKobo, string Currency, string Status, string? FrozenReason, DateTimeOffset CreatedAt);

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

// ---------- authentication & administration ----------

public sealed record RegisterCommand(string? Email, string? Password, string? FullName);

public sealed record LoginCommand(string? Email, string? Password);

public sealed record UserProfile(
    Guid UserId,
    string Email,
    string? FullName,
    string Role,
    string Status,
    string? DisabledReason,
    Guid? WalletId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt);

public sealed record AuthTokens(
    string AccessToken,
    string TokenType,
    int ExpiresIn,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    UserProfile User);

public sealed record UserPage(IReadOnlyList<UserProfile> Items, string? NextCursor);

public sealed record AdminActionItem(
    long Id, string ActorId, string Action, string TargetType, string TargetId, string? Detail,
    string? CorrelationId, DateTimeOffset OccurredAt);

public sealed record AdminActionPage(IReadOnlyList<AdminActionItem> Items, string? NextCursor);
