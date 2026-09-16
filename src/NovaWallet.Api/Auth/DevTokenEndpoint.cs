using Microsoft.Extensions.Options;

namespace NovaWallet.Api.Auth;

public static class DevTokenEndpoint
{
    public sealed record DevTokenRequest(string Subject, string? Role, int? LifetimeMinutes);

    public sealed record DevTokenResponse(string AccessToken, string TokenType, int ExpiresIn);

    public static void MapDevTokenEndpoint(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<IOptions<JwtOptions>>().Value.EnableDevTokenIssuer)
            return;

        app.Logger.LogWarning("Mock token issuer POST /dev/token is ENABLED. Never enable this outside local/test environments.");

        app.MapPost("/dev/token", (DevTokenRequest request, DevTokenIssuer issuer) =>
            {
                if (string.IsNullOrEmpty(request.Subject) || !AuthSetup.SubjectPattern().IsMatch(request.Subject))
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["subject"] = ["subject must be 1-64 characters of letters, digits, '-' or '_'."],
                    });

                var role = request.Role ?? Roles.Customer;
                if (role is not (Roles.Customer or Roles.Operator))
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["role"] = [$"role must be '{Roles.Customer}' or '{Roles.Operator}'."],
                    });

                var lifetime = TimeSpan.FromMinutes(Math.Clamp(request.LifetimeMinutes ?? 60, 1, (int)DevTokenIssuer.MaxLifetime.TotalMinutes));
                var token = issuer.Issue(request.Subject, role, lifetime);
                return Results.Ok(new DevTokenResponse(token, "Bearer", (int)lifetime.TotalSeconds));
            })
            .AllowAnonymous()
            .WithTags("Dev")
            .WithSummary("Mint a test JWT (mock issuer; disabled unless Jwt:EnableDevTokenIssuer=true).")
            .Produces<DevTokenResponse>()
            .ProducesValidationProblem();
    }
}
