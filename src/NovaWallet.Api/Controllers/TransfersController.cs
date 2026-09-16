using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NovaWallet.Api.Auth;
using NovaWallet.Api.Infrastructure;
using NovaWallet.Application;

namespace NovaWallet.Api.Controllers;

[ApiController]
[Route("api/v1/transfers")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
public sealed class TransfersController(TransferService transfers) : ControllerBase
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    public sealed record TransferRequest(
        [Required] Guid? SourceWalletId,
        [Required] Guid? DestinationWalletId,
        [Required, Range(1, long.MaxValue)] long? AmountKobo,
        [StringLength(RequestGuard.MaxNarrationLength)] string? Narration);

    /// <summary>
    /// Transfer between wallets. The caller must own the source wallet.
    /// Requires an <c>Idempotency-Key</c> header (a UUID is recommended): retrying with the same key and body
    /// returns the original result with <c>Idempotent-Replayed: true</c>; the same key with a different body
    /// is rejected with 422.
    /// </summary>
    [HttpPost]
    [EnableRateLimiting(RateLimitingSetup.TransfersPolicy)]
    [ProducesResponseType<TransactionReceipt>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Create(
        [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
        [FromBody] TransferRequest request,
        CancellationToken ct)
    {
        var command = new TransferCommand(
            request.SourceWalletId!.Value, request.DestinationWalletId!.Value, request.AmountKobo!.Value, request.Narration);

        var result = await transfers.TransferAsync(
            User.ToActor(), idempotencyKey, command, HttpContext.GetCorrelationId(), ct);
        return this.IdempotentCreated(result);
    }
}
