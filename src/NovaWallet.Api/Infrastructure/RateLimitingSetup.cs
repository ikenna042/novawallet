using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NovaWallet.Api.Auth;

namespace NovaWallet.Api.Infrastructure;

public sealed class TransferRateLimitOptions
{
    public const string SectionName = "RateLimiting:Transfers";

    public int PermitLimit { get; set; } = 20;
    public int WindowSeconds { get; set; } = 60;
}

/// <summary>Sign-up, sign-in and token refresh: limits password guessing and account spraying per client IP.</summary>
public sealed class AuthRateLimitOptions
{
    public const string SectionName = "RateLimiting:Auth";

    public int PermitLimit { get; set; } = 10;
    public int WindowSeconds { get; set; } = 60;
}

public static class RateLimitingSetup
{
    public const string TransfersPolicy = "transfers";
    public const string AuthPolicy = "auth";

    public static IServiceCollection AddLedgerRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<TransferRateLimitOptions>()
            .Bind(configuration.GetSection(TransferRateLimitOptions.SectionName))
            .Validate(o => o.PermitLimit > 0 && o.WindowSeconds > 0, "Rate limit settings must be positive.")
            .ValidateOnStart();

        services.AddOptions<AuthRateLimitOptions>()
            .Bind(configuration.GetSection(AuthRateLimitOptions.SectionName))
            .Validate(o => o.PermitLimit > 0 && o.WindowSeconds > 0, "Rate limit settings must be positive.")
            .ValidateOnStart();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Partition by authenticated customer so one noisy client can't starve others
            // (falling back to IP only for requests that somehow arrive unauthenticated).
            options.AddPolicy(TransfersPolicy, http =>
            {
                var settings = http.RequestServices.GetRequiredService<IOptions<TransferRateLimitOptions>>().Value;
                return RateLimitPartition.GetFixedWindowLimiter(
                    http.User.FindFirstValue(ClaimNames.Subject) ?? "ip:" + http.Connection.RemoteIpAddress,
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = settings.PermitLimit,
                        Window = TimeSpan.FromSeconds(settings.WindowSeconds),
                        QueueLimit = 0,
                    });
            });

            // Anonymous endpoints: the only stable key is the client address (behind a proxy, configure
            // ForwardedHeaders so this is the real client IP).
            options.AddPolicy(AuthPolicy, http =>
            {
                var settings = http.RequestServices.GetRequiredService<IOptions<AuthRateLimitOptions>>().Value;
                return RateLimitPartition.GetFixedWindowLimiter(
                    "ip:" + http.Connection.RemoteIpAddress,
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = settings.PermitLimit,
                        Window = TimeSpan.FromSeconds(settings.WindowSeconds),
                        QueueLimit = 0,
                    });
            });

            options.OnRejected = async (context, ct) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

                var problems = context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
                await problems.WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = context.HttpContext,
                    ProblemDetails = new ProblemDetails
                    {
                        Status = StatusCodes.Status429TooManyRequests,
                        Type = DomainExceptionHandler.ProblemTypeBase + "rate_limited",
                        Title = "Too many requests",
                        Detail = "Rate limit exceeded. Retry after the indicated delay.",
                        Extensions = { ["code"] = "rate_limited" },
                    },
                });
            };
        });

        return services;
    }
}
