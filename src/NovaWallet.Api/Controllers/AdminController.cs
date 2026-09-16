using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NovaWallet.Api.Auth;
using NovaWallet.Api.Infrastructure;
using NovaWallet.Application;

namespace NovaWallet.Api.Controllers;

/// <summary>
/// Administration. Admins can also use the wallet endpoints for any wallet (view, statement, credit, audit).
/// Every change here is recorded in the append-only admin action log.
/// </summary>
[ApiController]
[Route("api/v1/admin")]
[Authorize(Policy = AuthSetup.AdminPolicy)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
public sealed class AdminController(AdminService admin) : ControllerBase
{
    public sealed record ReasonRequest([Required, StringLength(200)] string? Reason);

    public sealed record RoleRequest([Required] string? Role);

    /// <summary>List users ordered by email. <c>email</c> filters by substring; pass <c>nextCursor</c> as <c>cursor</c>.</summary>
    [HttpGet("users")]
    [ProducesResponseType<UserPage>(StatusCodes.Status200OK)]
    public Task<UserPage> ListUsers(
        [FromQuery] string? email, [FromQuery] int? limit, [FromQuery] string? cursor, CancellationToken ct) =>
        admin.ListUsersAsync(User.ToActor(), email, limit, cursor, ct);

    /// <summary>A user's profile, status, role and wallet id (use the wallet endpoints to see balance and statement).</summary>
    [HttpGet("users/{userId:guid}")]
    [ProducesResponseType<UserProfile>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<UserProfile> GetUser(Guid userId, CancellationToken ct) =>
        admin.GetUserAsync(User.ToActor(), userId, ct);

    /// <summary>Disable an account: sign-in is refused and existing tokens stop working immediately.</summary>
    [HttpPost("users/{userId:guid}/disable")]
    [ProducesResponseType<UserProfile>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<UserProfile> DisableUser(Guid userId, [FromBody] ReasonRequest request, CancellationToken ct) =>
        admin.DisableUserAsync(User.ToActor(), userId, request.Reason, HttpContext.GetCorrelationId(), ct);

    [HttpPost("users/{userId:guid}/enable")]
    [ProducesResponseType<UserProfile>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<UserProfile> EnableUser(Guid userId, CancellationToken ct) =>
        admin.EnableUserAsync(User.ToActor(), userId, HttpContext.GetCorrelationId(), ct);

    /// <summary>
    /// Set a user's role to <c>admin</c> or <c>customer</c>. The user must sign in again to use the new role.
    /// You can't demote yourself, and at least one active admin must remain.
    /// </summary>
    [HttpPost("users/{userId:guid}/role")]
    [ProducesResponseType<UserProfile>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<UserProfile> ChangeRole(Guid userId, [FromBody] RoleRequest request, CancellationToken ct) =>
        admin.ChangeRoleAsync(User.ToActor(), userId, request.Role, HttpContext.GetCorrelationId(), ct);

    /// <summary>Put a debit hold on a wallet: outbound transfers are refused with 422 wallet_frozen; credits still land.</summary>
    [HttpPost("wallets/{walletId:guid}/freeze")]
    [ProducesResponseType<WalletResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<WalletResponse> FreezeWallet(Guid walletId, [FromBody] ReasonRequest request, CancellationToken ct) =>
        admin.FreezeWalletAsync(User.ToActor(), walletId, request.Reason, HttpContext.GetCorrelationId(), ct);

    [HttpPost("wallets/{walletId:guid}/unfreeze")]
    [ProducesResponseType<WalletResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<WalletResponse> UnfreezeWallet(Guid walletId, CancellationToken ct) =>
        admin.UnfreezeWalletAsync(User.ToActor(), walletId, HttpContext.GetCorrelationId(), ct);

    /// <summary>The append-only log of administrative actions, newest first.</summary>
    [HttpGet("actions")]
    [ProducesResponseType<AdminActionPage>(StatusCodes.Status200OK)]
    public Task<AdminActionPage> ListActions([FromQuery] int? limit, [FromQuery] string? cursor, CancellationToken ct) =>
        admin.ListActionsAsync(User.ToActor(), limit, cursor, ct);
}
