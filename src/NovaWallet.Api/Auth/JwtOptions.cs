using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Options;

namespace NovaWallet.Api.Auth;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public const int MinimumKeyBytes = 32;

    public string Issuer { get; set; } = "novawallet-dev-issuer";
    public string Audience { get; set; } = "novawallet-ledger";

    /// <summary>HMAC-SHA256 key. Supplied via environment/secret store; never committed for real environments.</summary>
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>Enables POST /dev/token, a mock issuer for local testing. Must be off anywhere real.</summary>
    public bool EnableDevTokenIssuer { get; set; }

    public SymmetricSecurityKey GetSigningKey() => new(Encoding.UTF8.GetBytes(SigningKey));
}

public static class Roles
{
    public const string Customer = "customer";
    public const string Operator = "operator";
}

public static class ClaimNames
{
    public const string Subject = "sub";
    public const string Role = "role";
}

/// <summary>
/// Mock token issuer so the service can be exercised without an identity provider. In production tokens
/// would come from the bank's IdP (asymmetric keys discovered via JWKS), and this class would not exist.
/// </summary>
public sealed class DevTokenIssuer(IOptions<JwtOptions> options, TimeProvider timeProvider)
{
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(1);

    public string Issue(string subject, string role, TimeSpan lifetime)
    {
        var jwt = options.Value;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer,
            Audience = jwt.Audience,
            Subject = new ClaimsIdentity([new Claim(ClaimNames.Subject, subject), new Claim(ClaimNames.Role, role)]),
            NotBefore = now,
            IssuedAt = now,
            Expires = now.Add(lifetime),
            SigningCredentials = new SigningCredentials(jwt.GetSigningKey(), SecurityAlgorithms.HmacSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
