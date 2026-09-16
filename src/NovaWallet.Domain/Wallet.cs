namespace NovaWallet.Domain;

public enum WalletStatus
{
    Active = 1,

    /// <summary>Debit hold: outbound transfers are refused; inbound credits still land.</summary>
    Frozen = 2,
}

public sealed class Wallet
{
    private Wallet() { } // EF Core

    public Wallet(Guid id, string customerId, DateTimeOffset createdAt)
    {
        if (string.IsNullOrWhiteSpace(customerId))
            throw new ArgumentException("Customer id is required.", nameof(customerId));

        Id = id;
        CustomerId = customerId;
        Currency = Money.Currency;
        BalanceKobo = 0;
        Status = WalletStatus.Active;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public string CustomerId { get; private set; } = null!;
    public string Currency { get; private set; } = Money.Currency;

    /// <summary>Persisted as bigint kobo; the database also enforces CHECK (balance_kobo >= 0).</summary>
    public long BalanceKobo { get; private set; }

    public WalletStatus Status { get; private set; }
    public string? FrozenReason { get; private set; }
    public DateTimeOffset? FrozenAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public Money Balance => Money.FromKobo(BalanceKobo);

    public bool IsOwnedBy(string customerId) => string.Equals(CustomerId, customerId, StringComparison.Ordinal);

    /// <summary>
    /// Must only be called while this wallet's row lock is held (see ILedgerStore.LockWalletsAsync);
    /// otherwise the balance read here may already be stale.
    /// </summary>
    public void Debit(Money amount, DateTimeOffset at)
    {
        EnsurePositive(amount);
        if (Status == WalletStatus.Frozen)
            throw new WalletFrozenException();
        if (Balance < amount)
            throw new InsufficientFundsException();
        BalanceKobo = (Balance - amount).Kobo;
        UpdatedAt = at;
    }

    public void Credit(Money amount, DateTimeOffset at)
    {
        EnsurePositive(amount);
        BalanceKobo = (Balance + amount).Kobo;
        UpdatedAt = at;
    }

    /// <summary>Must be called while holding the wallet's row lock so it serialises with in-flight transfers.</summary>
    public void Freeze(string reason, DateTimeOffset at)
    {
        Status = WalletStatus.Frozen;
        FrozenReason = reason;
        FrozenAt = at;
        UpdatedAt = at;
    }

    public void Unfreeze(DateTimeOffset at)
    {
        Status = WalletStatus.Active;
        FrozenReason = null;
        FrozenAt = null;
        UpdatedAt = at;
    }

    private static void EnsurePositive(Money amount)
    {
        if (amount.IsZero)
            throw new InvalidAmountException("Amount must be greater than zero.");
    }
}
