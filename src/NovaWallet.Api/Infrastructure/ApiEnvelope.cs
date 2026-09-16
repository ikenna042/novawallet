using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace NovaWallet.Api.Infrastructure;

/// <summary>
/// Every successful response body: <c>{ "statusCode": 201, "message": "Transfer completed", "data": { … } }</c>.
/// </summary>
public sealed record ApiResponse<T>(int StatusCode, string Message, T? Data);

/// <summary>
/// Every error body (documentation type). It starts with the same three properties as a success, followed by the
/// RFC 7807 Problem Details members, and is served as <c>application/problem+json</c>.
/// </summary>
public sealed class ApiError
{
    public int StatusCode { get; init; }
    public string Message { get; init; } = string.Empty;
    public object? Data { get; init; }
    public string? Type { get; init; }
    public string? Title { get; init; }
    public int? Status { get; init; }
    public string? Detail { get; init; }
    public string? Instance { get; init; }

    /// <summary>Stable machine-readable error code; branch on this, not on the message.</summary>
    public string Code { get; init; } = string.Empty;

    public string? TraceId { get; init; }
    public string? CorrelationId { get; init; }

    /// <summary>Field-level messages, present on validation errors only.</summary>
    public IDictionary<string, string[]>? Errors { get; init; }
}

/// <summary>Sets the success message for an action (defaults to the HTTP reason phrase).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ApiMessageAttribute(string message) : Attribute
{
    public string Message { get; } = message;
}

public static class ApiEnvelope
{
    public const string ProblemContentType = "application/problem+json";
    public const string ReplayedMessage = "Already processed: returning the original result.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The problem, with <c>statusCode</c>, <c>message</c> and <c>data</c> first. A <see cref="JsonObject"/> is used
    /// because it keeps property order, so the envelope reads the same for successes and errors.
    /// </summary>
    public static JsonObject FromProblem(ProblemDetails problem)
    {
        var status = problem.Status ?? StatusCodes.Status500InternalServerError;
        var body = new JsonObject
        {
            ["statusCode"] = status,
            ["message"] = problem.Detail ?? problem.Title ?? ReasonPhrases.GetReasonPhrase(status),
            ["data"] = null,
            ["type"] = problem.Type,
            ["title"] = problem.Title,
            ["status"] = status,
            ["detail"] = problem.Detail,
            ["instance"] = problem.Instance,
        };
        foreach (var (key, value) in problem.Extensions)
            body[key] = JsonSerializer.SerializeToNode(value, Json);
        if (problem is ValidationProblemDetails validation)
            body["errors"] = JsonSerializer.SerializeToNode(validation.Errors, Json);
        return body;
    }

    public static ApiResponse<T> Success<T>(int statusCode, string message, T? data) => new(statusCode, message, data);

    /// <summary>
    /// Fills in what ASP.NET leaves empty for bare status codes (401/403/404/405, model validation), so every error
    /// has a helpful message and a <c>code</c>.
    /// </summary>
    public static void Normalize(ProblemDetails problem)
    {
        var status = problem.Status ?? StatusCodes.Status500InternalServerError;
        problem.Detail ??= status switch
        {
            StatusCodes.Status401Unauthorized =>
                "Authentication required: sign in and send 'Authorization: Bearer <accessToken>'. The token may be missing, expired or revoked.",
            StatusCodes.Status403Forbidden => "You don't have permission to perform this action.",
            StatusCodes.Status404NotFound => "The requested resource was not found.",
            StatusCodes.Status405MethodNotAllowed => "This HTTP method is not supported for this resource.",
            StatusCodes.Status400BadRequest when problem is ValidationProblemDetails =>
                "The request is invalid. See 'errors' for details.",
            _ => null,
        };
        if (!problem.Extensions.ContainsKey("code"))
        {
            problem.Extensions["code"] = status switch
            {
                StatusCodes.Status400BadRequest => "validation_error",
                StatusCodes.Status401Unauthorized => "unauthorized",
                StatusCodes.Status403Forbidden => "forbidden",
                StatusCodes.Status404NotFound => "not_found",
                StatusCodes.Status405MethodNotAllowed => "method_not_allowed",
                _ => "error",
            };
        }
    }
}

/// <summary>
/// Writes every Problem Details produced outside MVC (exception handler, 401/403 from authentication, rate limiter,
/// status-code pages) in the envelope shape.
/// </summary>
public sealed class EnvelopeProblemDetailsWriter(IOptions<ProblemDetailsOptions> options) : IProblemDetailsWriter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool CanWrite(ProblemDetailsContext context) => true;

    public ValueTask WriteAsync(ProblemDetailsContext context)
    {
        var http = context.HttpContext;
        context.ProblemDetails.Status ??= http.Response.StatusCode;
        options.Value.CustomizeProblemDetails?.Invoke(context);

        return new ValueTask(http.Response.WriteAsJsonAsync(
            ApiEnvelope.FromProblem(context.ProblemDetails), Json, ApiEnvelope.ProblemContentType, http.RequestAborted));
    }
}

/// <summary>
/// Wraps controller results in the envelope: success values become <see cref="ApiResponse{T}"/>, and Problem
/// Details returned by MVC itself (automatic model validation) get the same shape as every other error.
/// </summary>
public sealed class ApiEnvelopeResultFilter : IAsyncAlwaysRunResultFilter
{
    public Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.Result is ObjectResult result)
        {
            switch (result.Value)
            {
                case JsonObject:
                case ApiResponse<object?>:
                    break;

                case ProblemDetails problem:
                    problem.Status ??= result.StatusCode;
                    ApiEnvelope.Normalize(problem);
                    result.Value = ApiEnvelope.FromProblem(problem);
                    result.DeclaredType = null;
                    result.ContentTypes.Clear();
                    result.ContentTypes.Add(ApiEnvelope.ProblemContentType);
                    break;

                default:
                    var status = result.StatusCode ?? StatusCodes.Status200OK;
                    var replayed = context.HttpContext.Response.Headers[ControllerExtensions.ReplayedHeader] == "true";
                    var message = replayed
                        ? ApiEnvelope.ReplayedMessage
                        : context.ActionDescriptor.EndpointMetadata.OfType<ApiMessageAttribute>().FirstOrDefault()?.Message
                          ?? ReasonPhrases.GetReasonPhrase(status);
                    result.StatusCode = status;
                    result.Value = ApiEnvelope.Success<object?>(status, message, result.Value);
                    result.DeclaredType = null;
                    break;
            }
        }

        return next();
    }
}
