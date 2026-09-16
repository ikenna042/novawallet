using System.Globalization;

namespace NovaWallet.Domain;

/// <summary>
/// An amount of Naira held as a whole number of kobo (₦1 = 100 kobo).
/// Integer-only by design: there is no float/double/decimal anywhere in the money path,
/// and all arithmetic is <c>checked</c> so an overflow throws instead of silently wrapping.
/// </summary>
public readonly record struct Money : IComparable<Money>
{
    public const string Currency = "NGN";
    public const long KoboPerNaira = 100;

    public long Kobo { get; }

    private Money(long kobo) => Kobo = kobo;

    public static Money Zero => new(0);

    public static Money FromKobo(long kobo)
    {
        if (kobo < 0)
            throw new InvalidAmountException("Amount cannot be negative.");
        return new Money(kobo);
    }

    public static Money FromNaira(long naira) => FromKobo(checked(naira * KoboPerNaira));

    public bool IsZero => Kobo == 0;

    public static Money operator +(Money a, Money b) => new(checked(a.Kobo + b.Kobo));

    public static Money operator -(Money a, Money b)
    {
        var result = checked(a.Kobo - b.Kobo);
        if (result < 0)
            throw new InvalidOperationException("Money cannot go negative; check the balance before subtracting.");
        return new Money(result);
    }

    public static bool operator >(Money a, Money b) => a.Kobo > b.Kobo;
    public static bool operator <(Money a, Money b) => a.Kobo < b.Kobo;
    public static bool operator >=(Money a, Money b) => a.Kobo >= b.Kobo;
    public static bool operator <=(Money a, Money b) => a.Kobo <= b.Kobo;

    public int CompareTo(Money other) => Kobo.CompareTo(other.Kobo);

    /// <summary>Formats as e.g. "₦1,234.05" using integer division only.</summary>
    public override string ToString()
    {
        var naira = Kobo / KoboPerNaira;
        var kobo = Kobo % KoboPerNaira;
        return string.Create(CultureInfo.InvariantCulture, $"₦{naira:N0}.{kobo:D2}");
    }
}
