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

    /// <param name="DestinationWalletId">The wallet receiving the money.</param>
    /// <param name="AmountKobo">Whole kobo (₦1 = 100 kobo).</param>
    /// <param name="Narration">Optional note shown on both statements (max 100 characters).</param>
    public sealed record TransferRequest(
        [Required] Guid? DestinationWalletId,
        [Required, Range(1, long.MaxValue)] long? AmountKobo,
        [StringLength(RequestGuard.MaxNarrationLength)] string? Narration);

    /// <summary>
    /// Send money from your own wallet (taken from your sign-in, never from the request) to another wallet.
    /// </summary>
    /// <remarks>
    /// **Idempotency-Key** is a header, not part of the transfer. Generate a new unique value (a UUID) for each
    /// transfer you intend to make, and reuse that same value if you have to retry it (for example after a
    /// timeout). The server then returns the original result, with <c>Idempotent-Replayed: true</c>, instead of
    /// sending the money twice. Reusing a key with a different body is rejected with 422.
    /// </remarks>
    /// <param name="idempotencyKey">A unique value per intended transfer, e.g. a UUID. Reuse it only to retry that same transfer.</param>
    /// <param name="request">Where to send the money and how much.</param>
    /// <param name="ct">Cancellation token.</param>
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
        var command = new TransferCommand(request.DestinationWalletId!.Value, request.AmountKobo!.Value, request.Narration);

        var result = await transfers.TransferAsync(
            User.ToActor(), idempotencyKey, command, HttpContext.GetCorrelationId(), ct);
        return this.IdempotentCreated(result);
    }
}
