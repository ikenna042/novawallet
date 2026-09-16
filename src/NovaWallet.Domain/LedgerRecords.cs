namespace NovaWallet.Domain;

public enum TransactionType
{
    Credit = 1,
    Transfer = 2,
}

public enum EntryDirection
{
    Debit = 1,
    Credit = 2,
}

/// <summary>One business event that moved money (an inbound credit or a wallet-to-wallet transfer).</summary>
public sealed class LedgerTransaction
{
    private LedgerTransaction() { } // EF Core

    public LedgerTransaction(
        Guid id,
        TransactionType type,
        Guid? sourceWalletId,
        Guid destinationWalletId,
        Money amount,
        string reference,
        string? narration,
        DateTimeOffset createdAt)
    {
        if (amount.IsZero)
            throw new InvalidAmountException("Amount must be greater than zero.");

        Id = id;
        Type = type;
        SourceWalletId = sourceWalletId;
        DestinationWalletId = destinationWalletId;
        AmountKobo = amount.Kobo;
        Reference = reference;
        Narration = narration;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public TransactionType Type { get; private set; }
    public Guid? SourceWalletId { get; private set; }
    public Guid DestinationWalletId { get; private set; }
    public long AmountKobo { get; private set; }

    /// <summary>Unique. For credits this is the upstream NIP session id; for transfers it is generated.</summary>
    public string Reference { get; private set; } = null!;

    public string? Narration { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}

/// <summary>
/// One side of a double-entry posting. Every transaction produces entries whose debits and credits net to zero
/// (a credit from outside the ledger has a single entry; its other side lives in the NIP settlement account,
/// which is out of scope). The statement endpoint reads from this table.
/// </summary>
public sealed class LedgerEntry
{
    private LedgerEntry() { } // EF Core

    public LedgerEntry(
        Guid walletId,
        Guid transactionId,
        EntryDirection direction,
        Money amount,
        Money balanceAfter,
        Guid? counterpartyWalletId,
        DateTimeOffset createdAt)
    {
        WalletId = walletId;
        TransactionId = transactionId;
        Direction = direction;
        AmountKobo = amount.Kobo;
        BalanceAfterKobo = balanceAfter.Kobo;
        CounterpartyWalletId = counterpartyWalletId;
        CreatedAt = createdAt;
    }

    /// <summary>Database-generated, monotonically increasing. Used as the statement's keyset cursor.</summary>
    public long Id { get; private set; }

    public Guid WalletId { get; private set; }
    public Guid TransactionId { get; private set; }
    public EntryDirection Direction { get; private set; }
    public long AmountKobo { get; private set; }
    public long BalanceAfterKobo { get; private set; }
    public Guid? CounterpartyWalletId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public LedgerTransaction? Transaction { get; private set; }
}

public sealed class OutboxMessage
{
    private OutboxMessage() { } // EF Core

    public OutboxMessage(Guid id, string type, string payload, DateTimeOffset occurredAt)
    {
        Id = id;
        Type = type;
        Payload = payload;
        OccurredAt = occurredAt;
    }

    public Guid Id { get; private set; }
    public string Type { get; private set; } = null!;
    public string Payload { get; private set; } = null!;
    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset? ProcessedAt { get; private set; }
    public int Attempts { get; private set; }
    public string? LastError { get; private set; }

    public void MarkProcessed(DateTimeOffset at)
    {
        ProcessedAt = at;
        Attempts++;
        LastError = null;
    }

    public void MarkFailed(string error)
    {
        Attempts++;
        LastError = error.Length > 2000 ? error[..2000] : error;
    }
}
