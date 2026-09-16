namespace NovaWallet.Application;

public sealed class LedgerOptions
{
    public const string SectionName = "Ledger";

    /// <summary>Maximum total outbound transfers per wallet per WAT calendar day. Default ₦500,000.</summary>
    public long DailyTransferLimitKobo { get; set; } = 500_000_00;

    /// <summary>Upper bound on any single credit or transfer. Default ₦100,000,000.</summary>
    public long MaxTransactionAmountKobo { get; set; } = 100_000_000_00;
}
