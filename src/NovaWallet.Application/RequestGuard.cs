using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using NovaWallet.Domain;

namespace NovaWallet.Application;

public sealed partial class RequestGuard(IOptions<LedgerOptions> options)
{
    public const int MaxNarrationLength = 100;

    public Money Amount(long amountKobo)
    {
        if (amountKobo <= 0)
            throw new InvalidAmountException("amountKobo must be a positive whole number of kobo.");
        if (amountKobo > options.Value.MaxTransactionAmountKobo)
            throw new InvalidAmountException(
                $"amountKobo exceeds the per-transaction maximum of {Money.FromKobo(options.Value.MaxTransactionAmountKobo)}.");
        return Money.FromKobo(amountKobo);
    }

    public static string IdempotencyKey(string? key)
    {
        if (string.IsNullOrEmpty(key) || !SafeToken().IsMatch(key))
            throw new RequestValidationException(
                "The Idempotency-Key header is required: 8-64 characters of letters, digits, '-' or '_' (a UUID is recommended).");
        return key;
    }

    public static string Reference(string? reference)
    {
        if (string.IsNullOrEmpty(reference) || !SafeToken().IsMatch(reference))
            throw new RequestValidationException("reference is required: 8-64 characters of letters, digits, '-' or '_'.");
        return reference;
    }

    public static string? Narration(string? narration)
    {
        if (narration is null)
            return null;
        narration = narration.Trim();
        if (narration.Length > MaxNarrationLength || narration.Any(char.IsControl))
            throw new RequestValidationException($"narration must be at most {MaxNarrationLength} printable characters.");
        return narration.Length == 0 ? null : narration;
    }

    /// <summary>SHA-256 over an unambiguous, culture-invariant encoding of the request fields.</summary>
    public static string HashRequest(params object?[] fields)
    {
        var canonical = string.Join('', fields.Select(f => f switch
        {
            null => "␀",
            Guid g => g.ToString("N"),
            IFormattable x => x.ToString(null, CultureInfo.InvariantCulture),
            _ => f.ToString(),
        }));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{8,64}$")]
    private static partial Regex SafeToken();
}
