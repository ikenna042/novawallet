using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NovaWallet.Domain;

namespace NovaWallet.Application;

public static partial class Credentials
{
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 128;
    public const int MaxEmailLength = 254;
    public const int MaxFullNameLength = 100;

    public static string Email(string? email)
    {
        var normalized = User.NormalizeEmail(email ?? string.Empty);
        if (normalized.Length is 0 or > MaxEmailLength || !EmailPattern().IsMatch(normalized))
            throw new RequestValidationException("email must be a valid email address.");
        return normalized;
    }

    /// <summary>
    /// Length first (NIST SP 800-63B), plus a letter and a digit so trivially guessable values are refused.
    /// </summary>
    public static string Password(string? password)
    {
        if (password is null
            || password.Length is < MinPasswordLength or > MaxPasswordLength
            || !password.Any(char.IsLetter)
            || !password.Any(char.IsDigit)
            || password.Any(char.IsControl))
        {
            throw new RequestValidationException(
                $"password must be {MinPasswordLength}-{MaxPasswordLength} characters and contain at least one letter and one digit.");
        }
        return password;
    }

    public static string? FullName(string? fullName)
    {
        if (fullName is null)
            return null;
        fullName = fullName.Trim();
        if (fullName.Length > MaxFullNameLength || fullName.Any(char.IsControl))
            throw new RequestValidationException($"fullName must be at most {MaxFullNameLength} printable characters.");
        return fullName.Length == 0 ? null : fullName;
    }

    public static string Reason(string? reason)
    {
        reason = reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > 200 || reason.Any(char.IsControl))
            throw new RequestValidationException("reason is required (1-200 printable characters).");
        return reason;
    }

    /// <summary>A new opaque refresh token (256 bits, URL-safe) and the hash that gets stored.</summary>
    public static (string Token, string Hash) NewRefreshToken()
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        return (token, HashRefreshToken(token));
    }

    public static string HashRefreshToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // Pragmatic check: one @, no spaces, a dot in the domain. Real verification is an emailed link.
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();
}
