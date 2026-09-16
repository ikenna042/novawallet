using NovaWallet.Domain;

namespace NovaWallet.UnitTests;

public class MoneyTests
{
    [Theory]
    [InlineData(0, "₦0.00")]
    [InlineData(5, "₦0.05")]
    [InlineData(123405, "₦1,234.05")]
    [InlineData(50000000, "₦500,000.00")]
    public void Formats_kobo_as_naira_without_floating_point(long kobo, string expected) =>
        Assert.Equal(expected, Money.FromKobo(kobo).ToString());

    [Fact]
    public void Rejects_negative_amounts() =>
        Assert.Throws<InvalidAmountException>(() => Money.FromKobo(-1));

    [Fact]
    public void Addition_overflow_throws_instead_of_wrapping() =>
        Assert.Throws<OverflowException>(() => Money.FromKobo(long.MaxValue) + Money.FromKobo(1));

    [Fact]
    public void Subtraction_below_zero_throws() =>
        Assert.Throws<InvalidOperationException>(() => Money.FromKobo(99) - Money.FromKobo(100));

    [Fact]
    public void FromNaira_converts_to_kobo() =>
        Assert.Equal(500_000_00, Money.FromNaira(500_000).Kobo);
}

public class WalletTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void New_wallet_starts_at_zero_ngn()
    {
        var wallet = new Wallet(Guid.NewGuid(), "cust-1", Now);
        Assert.Equal(0, wallet.BalanceKobo);
        Assert.Equal("NGN", wallet.Currency);
    }

    [Fact]
    public void Debit_more_than_balance_throws_and_leaves_balance_unchanged()
    {
        var wallet = new Wallet(Guid.NewGuid(), "cust-1", Now);
        wallet.Credit(Money.FromKobo(100), Now);

        Assert.Throws<InsufficientFundsException>(() => wallet.Debit(Money.FromKobo(101), Now));
        Assert.Equal(100, wallet.BalanceKobo);
    }

    [Fact]
    public void Debit_of_exact_balance_leaves_zero()
    {
        var wallet = new Wallet(Guid.NewGuid(), "cust-1", Now);
        wallet.Credit(Money.FromKobo(100), Now);
        wallet.Debit(Money.FromKobo(100), Now);
        Assert.Equal(0, wallet.BalanceKobo);
    }

    [Fact]
    public void Zero_amounts_are_rejected()
    {
        var wallet = new Wallet(Guid.NewGuid(), "cust-1", Now);
        Assert.Throws<InvalidAmountException>(() => wallet.Credit(Money.Zero, Now));
        Assert.Throws<InvalidAmountException>(() => wallet.Debit(Money.Zero, Now));
    }
}

public class AuditChainTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid WalletId = Guid.NewGuid();

    private static List<AuditRecord> BuildChain()
    {
        var first = new AuditRecord(WalletId, Guid.NewGuid(), AuditActions.Credit, 1_000, 0, 1_000, "op", "c1", Now,
            AuditRecord.GenesisHash);
        var second = new AuditRecord(WalletId, Guid.NewGuid(), AuditActions.TransferDebit, -400, 1_000, 600, "cust", "c2",
            Now.AddSeconds(1), first.Hash);
        return [first, second];
    }

    [Fact]
    public void Intact_chain_verifies() => Assert.Null(AuditChain.Verify(BuildChain()));

    [Fact]
    public void Record_pointing_at_wrong_previous_hash_is_detected()
    {
        var chain = BuildChain();
        chain[1] = new AuditRecord(WalletId, Guid.NewGuid(), AuditActions.TransferDebit, -400, 1_000, 600, "cust", "c2",
            Now.AddSeconds(1), AuditRecord.GenesisHash);
        Assert.NotNull(AuditChain.Verify(chain));
    }

    [Fact]
    public void Removed_record_is_detected()
    {
        var chain = BuildChain();
        chain.RemoveAt(0);
        Assert.NotNull(AuditChain.Verify(chain));
    }

    [Fact]
    public void Record_that_does_not_add_up_cannot_be_created() =>
        Assert.Throws<InvalidOperationException>(() => new AuditRecord(
            WalletId, Guid.NewGuid(), AuditActions.Credit, 100, 0, 101, "op", null, Now, AuditRecord.GenesisHash));

    [Fact]
    public void Hash_changes_when_any_field_changes()
    {
        var txId = Guid.NewGuid();
        var a = new AuditRecord(WalletId, txId, AuditActions.Credit, 100, 0, 100, "op", null, Now, AuditRecord.GenesisHash);
        var b = new AuditRecord(WalletId, txId, AuditActions.Credit, 100, 0, 100, "op2", null, Now, AuditRecord.GenesisHash);
        Assert.NotEqual(a.Hash, b.Hash);
    }
}
