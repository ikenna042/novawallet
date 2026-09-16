using Microsoft.Extensions.Options;
using NovaWallet.Domain;

namespace NovaWallet.Application;

public sealed class DailyLimitPolicy(IOptions<LedgerOptions> options)
{
    /// <summary>
    /// West Africa Time is a fixed UTC+1 with no daylight saving. A fixed offset is used instead of a
    /// "Africa/Lagos" time-zone lookup so behaviour does not depend on tzdata being present in the container.
    /// </summary>
    public static readonly TimeSpan WatOffset = TimeSpan.FromHours(1);

    public Money DailyLimit => Money.FromKobo(options.Value.DailyTransferLimitKobo);

    /// <summary>The UTC instant of the most recent midnight in WAT.</summary>
    public static DateTimeOffset WindowStartUtc(DateTimeOffset now)
    {
        var wat = now.ToOffset(WatOffset);
        var midnightWat = new DateTimeOffset(wat.Year, wat.Month, wat.Day, 0, 0, 0, WatOffset);
        return midnightWat.ToUniversalTime();
    }

    public void EnsureWithinLimit(Money spentToday, Money amount)
    {
        var limit = DailyLimit;
        if (spentToday + amount <= limit)
            return;

        var remaining = spentToday >= limit ? Money.Zero : limit - spentToday;
        throw new DailyLimitExceededException(limit, remaining);
    }
}
