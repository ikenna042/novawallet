using System.Diagnostics;
using System.Text.RegularExpressions;
using Serilog.Context;

namespace NovaWallet.Api.Infrastructure;

/// <summary>
/// Accepts an inbound X-Correlation-ID (e.g. from the mobile app, USSD gateway or API gateway) or falls back to
/// the W3C trace id, echoes it on the response, and pushes it into every log event for the request.
/// </summary>
public sealed partial class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-ID";
    private const string ItemKey = "CorrelationId";

    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[HeaderName].ToString();
        var correlationId = SafeId().IsMatch(incoming)
            ? incoming
            : Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;

        context.Items[ItemKey] = correlationId;
        Activity.Current?.SetTag("correlation.id", correlationId);
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty(ItemKey, correlationId))
        {
            await next(context);
        }
    }

    public static string? Get(HttpContext context) => context.Items[ItemKey] as string;

    // Reject anything that could be used for log injection.
    [GeneratedRegex("^[A-Za-z0-9._-]{8,128}$")]
    private static partial Regex SafeId();
}

public static class HttpContextExtensions
{
    public static string? GetCorrelationId(this HttpContext context) => CorrelationIdMiddleware.Get(context);
}
