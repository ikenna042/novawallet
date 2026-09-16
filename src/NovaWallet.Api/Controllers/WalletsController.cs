using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using NovaWallet.Api.Auth;
using NovaWallet.Api.Infrastructure;
using NovaWallet.Application;

namespace NovaWallet.Api.Controllers;

[ApiController]
[Route("api/v1/wallets")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
public sealed class WalletsController(WalletService wallets) : ControllerBase
{
    public sealed record CreateWalletRequest([StringLength(64)] string? CustomerId);

    public sealed record CreditRequest(
        [Required, Range(1, long.MaxValue)] long? AmountKobo,
        [Required, StringLength(64, MinimumLength = 8)] string? Reference,
        [StringLength(RequestGuard.MaxNarrationLength)] string? Narration);

    /// <summary>Create a wallet (balance zero). Customers create their own; operators may pass customerId.</summary>
    [HttpPost]
    [ProducesResponseType<WalletResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] CreateWalletRequest? request, CancellationToken ct)
    {
        var wallet = await wallets.CreateAsync(User.ToActor(), new CreateWalletCommand(request?.CustomerId), ct);
        return CreatedAtAction(nameof(Get), new { walletId = wallet.WalletId }, wallet);
    }

    [HttpGet("{walletId:guid}")]
    [ProducesResponseType<WalletResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<WalletResponse> Get(Guid walletId, CancellationToken ct) =>
        wallets.GetAsync(User.ToActor(), walletId, ct);

    /// <summary>Current balance in kobo (NGN).</summary>
    [HttpGet("{walletId:guid}/balance")]
    [ProducesResponseType<BalanceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<BalanceResponse> GetBalance(Guid walletId, CancellationToken ct) =>
        wallets.GetBalanceAsync(User.ToActor(), walletId, ct);

    /// <summary>
    /// Credit a wallet, simulating an inbound NIP transfer. Admin role only. Idempotent on <c>reference</c>
    /// (the NIP session id): a repeat returns the original receipt with <c>Idempotent-Replayed: true</c>.
    /// </summary>
    [HttpPost("{walletId:guid}/credit")]
    [Authorize(Policy = AuthSetup.AdminPolicy)]
    [ProducesResponseType<TransactionReceipt>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Credit(Guid walletId, [FromBody] CreditRequest request, CancellationToken ct)
    {
        var result = await wallets.CreditAsync(
            User.ToActor(),
            walletId,
            new CreditCommand(request.AmountKobo!.Value, request.Reference, request.Narration),
            HttpContext.GetCorrelationId(),
            ct);
        return this.IdempotentCreated(result);
    }

    /// <summary>Transaction history, newest first. Pass <c>nextCursor</c> from the previous page as <c>cursor</c>.</summary>
    [HttpGet("{walletId:guid}/statement")]
    [ProducesResponseType<StatementPage>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<StatementPage> GetStatement(
        Guid walletId, [FromQuery] int? limit, [FromQuery] string? cursor, CancellationToken ct) =>
        wallets.GetStatementAsync(User.ToActor(), walletId, limit, cursor, ct);

    /// <summary>The wallet's append-only audit trail, with hash-chain verification. Admin role only.</summary>
    [HttpGet("{walletId:guid}/audit")]
    [Authorize(Policy = AuthSetup.AdminPolicy)]
    [ProducesResponseType<AuditTrailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<AuditTrailResponse> GetAudit(Guid walletId, CancellationToken ct) =>
        wallets.GetAuditTrailAsync(User.ToActor(), walletId, ct);
}
