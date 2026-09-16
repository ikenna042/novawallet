using System.Globalization;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NovaWallet.Application;
using NovaWallet.Application.Abstractions;
using NovaWallet.Domain;

namespace NovaWallet.Api.Auth;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public const int MinimumKeyBytes = 32;

    public string Issuer { get; set; } = "novawallet-ledger";
    public string Audience { get; set; } = "novawallet-ledger";

    /// <summary>HMAC-SHA256 key. Supplied via environment/secret store; never committed for real environments.</summary>
    public string SigningKey { get; set; } = string.Empty;

    public SymmetricSecurityKey GetSigningKey() => new(Encoding.UTF8.GetBytes(SigningKey));
}

public static class Roles
{
    public const string Customer = "customer";
    public const string Admin = "admin";
}

public static class ClaimNames
{
    public const string Subject = "sub";
    public const string Role = "role";

    /// <summary>The user's token version when the token was issued; see <see cref="User.TokenVersion"/>.</summary>
    public const string TokenVersion = "ver";
}

/// <summary>
/// Issues access tokens after a successful sign-in. HS256 is appropriate here because this service is both the
/// issuer and the only validator; with a separate identity provider this would become RS256 validated via JWKS.
/// </summary>
public sealed class JwtTokenIssuer(IOptions<JwtOptions> jwt, IOptions<AuthOptions> auth, TimeProvider timeProvider)
    : ITokenIssuer
{
    public IssuedAccessToken IssueAccessToken(User user) =>
        Issue(user.SubjectId, AuthService.RoleName(user.Role), user.TokenVersion,
            TimeSpan.FromMinutes(auth.Value.AccessTokenMinutes));

    /// <summary>Lower-level entry point, also used by tests to craft expired or foreign tokens.</summary>
    public IssuedAccessToken Issue(string subject, string role, int tokenVersion, TimeSpan lifetime)
    {
        var options = jwt.Value;
        var now = timeProvider.GetUtcNow();
        var expires = now.Add(lifetime);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            Subject = new ClaimsIdentity(
            [
                new Claim(ClaimNames.Subject, subject),
                new Claim(ClaimNames.Role, role),
                new Claim(ClaimNames.TokenVersion, tokenVersion.ToString(CultureInfo.InvariantCulture)),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            ]),
            NotBefore = now.UtcDateTime,
            IssuedAt = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(options.GetSigningKey(), SecurityAlgorithms.HmacSha256),
        };
        return new IssuedAccessToken(new JsonWebTokenHandler().CreateToken(descriptor), expires);
    }
}
