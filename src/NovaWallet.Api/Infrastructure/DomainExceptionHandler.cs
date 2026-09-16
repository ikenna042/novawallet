using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NovaWallet.Application.Abstractions;
using NovaWallet.Domain;

namespace NovaWallet.Api.Infrastructure;

/// <summary>
/// Last-resort handler: turns any exception that escapes the MVC pipeline into RFC 7807 Problem Details with a
/// stable <c>code</c> clients can branch on, without leaking internals. (Expected business rejections are
/// handled earlier by <see cref="DomainExceptionFilter"/>, because the .NET 8 exception middleware logs
/// everything that reaches it at Error level.)
/// </summary>
public sealed class DomainExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public const string ProblemTypeBase = "https://novawallet.example/problems/";

    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var (status, code, title, detail) = Map(exception);

        context.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails = Create(status, code, title, detail),
        });
    }

    public static ProblemDetails Create(int status, string code, string title, string? detail) => new()
    {
        Status = status,
        Type = ProblemTypeBase + code,
        Title = title,
        Detail = detail,
        Extensions = { ["code"] = code },
    };

    public static int StatusFor(string code) => code switch
    {
        ErrorCodes.Validation or ErrorCodes.InvalidAmount or ErrorCodes.SameWalletTransfer => StatusCodes.Status400BadRequest,
        ErrorCodes.InvalidCredentials or ErrorCodes.InvalidRefreshToken => StatusCodes.Status401Unauthorized,
        ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
        ErrorCodes.WalletNotFound or ErrorCodes.UserNotFound => StatusCodes.Status404NotFound,
        ErrorCodes.WalletAlreadyExists or ErrorCodes.DuplicateReference or ErrorCodes.EmailAlreadyRegistered
            or ErrorCodes.AdminRuleViolation => StatusCodes.Status409Conflict,
        ErrorCodes.InsufficientFunds or ErrorCodes.DailyLimitExceeded or ErrorCodes.IdempotencyKeyReused
            or ErrorCodes.WalletFrozen => StatusCodes.Status422UnprocessableEntity,
        _ => StatusCodes.Status400BadRequest,
    };

    internal static (int Status, string Code, string Title, string? Detail) Map(Exception exception) => exception switch
    {
        DomainException d => (StatusFor(d.Code), d.Code, TitleFor(d.Code), d.Message),
        BadHttpRequestException b => (b.StatusCode, "bad_request", "Bad request", "The request could not be read."),
        UniqueConstraintException => (StatusCodes.Status409Conflict, "conflict", "Conflict",
            "The request conflicts with existing data."),
        // Should be impossible: the application checks these rules first. Never leak database details.
        DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.CheckViolation } }
            => (StatusCodes.Status500InternalServerError,
            "invariant_violation", "Ledger invariant violated", "The request was refused to protect ledger integrity."),
        OperationCanceledException => (499, "client_closed_request", "Request cancelled", null),
        _ => (StatusCodes.Status500InternalServerError, "internal_error", "Internal server error",
            "An unexpected error occurred. Quote the correlation id when contacting support."),
    };

    private static string TitleFor(string code) => code switch
    {
        ErrorCodes.InvalidAmount => "Invalid amount",
        ErrorCodes.InsufficientFunds => "Insufficient funds",
        ErrorCodes.DailyLimitExceeded => "Daily transfer limit exceeded",
        ErrorCodes.WalletNotFound => "Wallet not found",
        ErrorCodes.WalletAlreadyExists => "Wallet already exists",
        ErrorCodes.SameWalletTransfer => "Invalid transfer",
        ErrorCodes.IdempotencyKeyReused => "Idempotency key reused",
        ErrorCodes.DuplicateReference => "Duplicate reference",
        ErrorCodes.Forbidden => "Forbidden",
        ErrorCodes.Validation => "Validation failed",
        ErrorCodes.InvalidCredentials => "Invalid credentials",
        ErrorCodes.InvalidRefreshToken => "Invalid refresh token",
        ErrorCodes.EmailAlreadyRegistered => "Email already registered",
        ErrorCodes.WalletFrozen => "Wallet frozen",
        ErrorCodes.UserNotFound => "User not found",
        ErrorCodes.AdminRuleViolation => "Not allowed",
        _ => "Request rejected",
    };
}
