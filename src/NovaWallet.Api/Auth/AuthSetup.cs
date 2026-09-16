using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NovaWallet.Application;

namespace NovaWallet.Api.Auth;

public static partial class AuthSetup
{
    public const string OperatorPolicy = "Operator";

    public static IServiceCollection AddLedgerAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .Validate(o => System.Text.Encoding.UTF8.GetByteCount(o.SigningKey) >= JwtOptions.MinimumKeyBytes,
                $"Jwt:SigningKey must be at least {JwtOptions.MinimumKeyBytes} bytes.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.Issuer) && !string.IsNullOrWhiteSpace(o.Audience),
                "Jwt:Issuer and Jwt:Audience are required.")
            .ValidateOnStart();

        services.AddSingleton<DevTokenIssuer>();

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
                bearer.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        var subject = context.Principal?.FindFirstValue(ClaimNames.Subject);
                        if (subject is null || !SubjectPattern().IsMatch(subject))
                            context.Fail("Token has a missing or malformed 'sub' claim.");
                        return Task.CompletedTask;
                    },
                };
            });

        services.AddAuthorizationBuilder()
            // Every endpoint requires a valid token unless it explicitly opts out.
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(OperatorPolicy, p => p.RequireAuthenticatedUser().RequireRole(Roles.Operator));

        return services;
    }

    public static Actor ToActor(this ClaimsPrincipal user)
    {
        var subject = user.FindFirstValue(ClaimNames.Subject)
                      ?? throw new InvalidOperationException("Authenticated user has no subject.");
        return new Actor(subject, user.IsInRole(Roles.Operator));
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    internal static partial Regex SubjectPattern();
}
