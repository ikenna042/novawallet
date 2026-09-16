using Microsoft.AspNetCore.Identity;
using NovaWallet.Application.Abstractions;
using NovaWallet.Domain;

namespace NovaWallet.Infrastructure.Auth;

/// <summary>
/// ASP.NET Core Identity's hasher (PBKDF2-HMAC-SHA512, 100,000 iterations, random salt) used on its own,
/// without the rest of the Identity stack. The hash format is versioned, so parameters can be raised later
/// and old hashes upgraded on the next successful sign-in.
/// </summary>
public sealed class IdentityPasswordHasher : NovaWallet.Application.Abstractions.IPasswordHasher
{
    private readonly PasswordHasher<User> _inner = new();

    public string Hash(string password) => _inner.HashPassword(null!, password);

    public bool Verify(string hash, string password, out bool needsRehash)
    {
        var result = _inner.VerifyHashedPassword(null!, hash, password);
        needsRehash = result == PasswordVerificationResult.SuccessRehashNeeded;
        return result != PasswordVerificationResult.Failed;
    }
}
