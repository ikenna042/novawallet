namespace NovaWallet.Application;

public static class LedgerClock
{
    /// <summary>
    /// Current UTC time truncated to whole microseconds, which is what PostgreSQL's timestamptz stores.
    /// Without this, a timestamp read back from the database would differ from the one written, and
    /// the audit hash chain would fail verification.
    /// </summary>
    public static DateTimeOffset GetLedgerNow(this TimeProvider timeProvider)
    {
        var now = timeProvider.GetUtcNow();
        return now.AddTicks(-(now.Ticks % 10));
    }
}
