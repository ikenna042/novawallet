using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NovaWallet.Application;
using NovaWallet.Application.Abstractions;
using NovaWallet.Domain;

namespace NovaWallet.Api.Auth;

public static class AuthSetup
{
    public const string AdminPolicy = "Admin";

    public static IServiceCollection AddLedgerAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .Validate(o => System.Text.Encoding.UTF8.GetByteCount(o.SigningKey) >= JwtOptions.MinimumKeyBytes,
                $"Jwt:SigningKey must be at least {JwtOptions.MinimumKeyBytes} bytes.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.Issuer) && !string.IsNullOrWhiteSpace(o.Audience),
                "Jwt:Issuer and Jwt:Audience are required.")
            .ValidateOnStart();

        services.AddSingleton<JwtTokenIssuer>();
        services.AddSingleton<ITokenIssuer>(sp => sp.GetRequiredService<JwtTokenIssuer>());

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((bearer, jwtOptions) =>
            {
                var jwt = jwtOptions.Value;
                // Keep claim names as issued ("sub", "role") instead of the legacy SOAP-style URIs.
                bearer.MapInboundClaims = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = jwt.GetSigningKey(),
                    // Pin the algorithm so a token can't pick a weaker one (or "none").
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = ClaimNames.Subject,
                    RoleClaimType = ClaimNames.Role,
                };
                bearer.Events = new JwtBearerEvents { OnTokenValidated = ValidateAgainstUserAsync };
            });

        services.AddAuthorizationBuilder()
            // Every endpoint requires a valid token unless it explicitly opts out.
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(AdminPolicy, p => p.RequireAuthenticatedUser().RequireRole(Roles.Admin));

        return services;
    }

    /// <summary>
    /// A valid signature isn't enough: the user must still exist, be active, and the token must carry the
    /// user's current token version and role. This is what makes "disable user" and "change role" take effect
    /// immediately instead of when the access token expires. Costs one primary-key lookup per request.
    /// </summary>
    private static async Task ValidateAgainstUserAsync(TokenValidatedContext context)
    {
        var principal = context.Principal!;
        var subject = principal.FindFirstValue(ClaimNames.Subject);
        var role = principal.FindFirstValue(ClaimNames.Role);
        if (!Guid.TryParseExact(subject, "N", out var userId)
            || !int.TryParse(principal.FindFirstValue(ClaimNames.TokenVersion), NumberStyles.None,
                CultureInfo.InvariantCulture, out var version))
        {
            context.Fail("Token is missing required claims.");
            return;
        }

        var users = context.HttpContext.RequestServices.GetRequiredService<IUserStore>();
        var state = await users.GetAuthStateAsync(userId, context.HttpContext.RequestAborted);
        if (state is null
            || state.Status != UserStatus.Active
            || state.TokenVersion != version
            || AuthService.RoleName(state.Role) != role)
        {
            context.Fail("Token has been revoked.");
        }
    }

    public static Actor ToActor(this ClaimsPrincipal user)
    {
        var subject = user.FindFirstValue(ClaimNames.Subject)
                      ?? throw new InvalidOperationException("Authenticated user has no subject.");
        return new Actor(subject, user.IsInRole(Roles.Admin));
    }
}
