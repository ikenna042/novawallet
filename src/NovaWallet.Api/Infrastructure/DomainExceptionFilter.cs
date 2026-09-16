using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using NovaWallet.Domain;

namespace NovaWallet.Api.Infrastructure;

/// <summary>
/// Converts expected business rejections (insufficient funds, limit exceeded, ...) into Problem Details inside
/// MVC, so they are logged at Information level instead of as unhandled errors that would page on-call.
/// </summary>
public sealed class DomainExceptionFilter(IOptions<ProblemDetailsOptions> options, ILogger<DomainExceptionFilter> logger)
    : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not DomainException exception)
            return;

        var (status, code, title, detail) = DomainExceptionHandler.Map(exception);
        var problem = DomainExceptionHandler.Create(status, code, title, detail);
        options.Value.CustomizeProblemDetails?.Invoke(new ProblemDetailsContext
        {
            HttpContext = context.HttpContext,
            ProblemDetails = problem,
            Exception = exception,
        });

        logger.LogInformation("Request rejected with {StatusCode} {ErrorCode}", status, code);
        context.Result = new ObjectResult(problem)
        {
            StatusCode = status,
            ContentTypes = { "application/problem+json" },
        };
        context.ExceptionHandled = true;
    }
}
