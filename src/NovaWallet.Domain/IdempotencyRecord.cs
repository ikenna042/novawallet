namespace NovaWallet.Domain;

public enum IdempotencyOutcome
{
    Succeeded = 1,
    Rejected = 2,
}

/// <summary>
/// Stored result of a request made with an Idempotency-Key. Keys are scoped to the calling customer,
/// so two customers can never collide on the same key.
/// </summary>
public sealed class IdempotencyRecord
{
    private IdempotencyRecord() { } // EF Core

    public IdempotencyRecord(string scope, string key, string requestHash, DateTimeOffset createdAt)
    {
        Scope = scope;
        Key = key;
        RequestHash = requestHash;
        CreatedAt = createdAt;
    }

    public void Complete(IdempotencyOutcome outcome, string? responseJson, string? errorCode, string? errorMessage,
        DateTimeOffset completedAt)
    {
        Outcome = outcome;
        ResponseJson = responseJson;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        CompletedAt = completedAt;
    }

    public string Scope { get; private set; } = null!;
    public string Key { get; private set; } = null!;

    /// <summary>SHA-256 of the canonical request, used to reject reuse of a key with a different payload.</summary>
    public string RequestHash { get; private set; } = null!;

    public IdempotencyOutcome? Outcome { get; private set; }
    public string? ResponseJson { get; private set; }
    public string? ErrorCode { get; private set; }
    public string? ErrorMessage { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
}
