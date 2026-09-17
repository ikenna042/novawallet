using System.Globalization;
using Microsoft.Extensions.Logging;
using NovaWallet.Application.Abstractions;
using NovaWallet.Domain;

namespace NovaWallet.Application;

/// <summary>
/// Administrative operations. Every change is written to the append-only admin action log in the same
/// transaction as the change itself.
/// </summary>
public sealed class AdminService(
    IUserStore users,
    ILedgerStore ledger,
    TimeProvider timeProvider,
    ILogger<AdminService> logger)
{
    public const int MaxPageSize = 100;

    public async Task<UserPage> ListUsersAsync(Actor actor, string? email, int? limit, string? cursor, CancellationToken ct)
    {
        EnsureAdmin(actor);
        var take = PageSize(limit);
        var filter = string.IsNullOrWhiteSpace(email) ? null : User.NormalizeEmail(email);
        if (filter is { Length: > Credentials.MaxEmailLength })
            throw new RequestValidationException("email filter is too long.");
        if (cursor is { Length: > Credentials.MaxEmailLength })
            throw new RequestValidationException("cursor is invalid.");

        var found = await users.ListAsync(filter, cursor, take + 1, ct);
        var items = new List<UserProfile>();
        foreach (var user in found.Take(take))
            items.Add(AuthService.ToProfile(user, await users.FindWalletIdAsync(user.SubjectId, ct)));

        return new UserPage(items, found.Count > take ? items[^1].Email : null);
    }

    public async Task<UserProfile> GetUserAsync(Actor actor, Guid userId, CancellationToken ct)
    {
        EnsureAdmin(actor);
        var user = await users.FindByIdAsync(userId, ct) ?? throw new UserNotFoundException(userId);
        return AuthService.ToProfile(user, await users.FindWalletIdAsync(user.SubjectId, ct));
    }

    /// <summary>Blocks sign-in and immediately invalidates every token the user holds.</summary>
    public async Task<UserProfile> DisableUserAsync(
        Actor actor, Guid userId, string? reason, string? correlationId, CancellationToken ct)
    {
        EnsureAdmin(actor);
        var why = Credentials.Reason(reason);
        if (userId == actor.UserId)
            throw new AdminRuleViolationException("You cannot disable your own account.");

        return await ChangeUserAsync(actor, userId, correlationId, ct, (user, activeAdmins, now) =>
        {
            if (user.IsAdmin && user.IsActive && activeAdmins.Count <= 1)
                throw new AdminRuleViolationException("At least one active administrator must remain.");
            user.Disable(why);
            return (AdminActionTypes.DisableUser, why, revokeSessions: true);
        });
    }

    public async Task<UserProfile> EnableUserAsync(Actor actor, Guid userId, string? correlationId, CancellationToken ct)
    {
        EnsureAdmin(actor);
        return await ChangeUserAsync(actor, userId, correlationId, ct, (user, _, _) =>
        {
            user.Enable();
            return (AdminActionTypes.EnableUser, null, revokeSessions: false);
        });
    }

    /// <summary>Promotes or demotes a user. Tokens carrying the old role stop working immediately.</summary>
    public async Task<UserProfile> ChangeRoleAsync(
        Actor actor, Guid userId, string? role, string? correlationId, CancellationToken ct)
    {
        EnsureAdmin(actor);
        var newRole = role?.Trim().ToLowerInvariant() switch
        {
            "admin" => UserRole.Admin,
            "customer" => UserRole.Customer,
            _ => throw new RequestValidationException("role must be 'admin' or 'customer'."),
        };
        if (userId == actor.UserId && newRole != UserRole.Admin)
            throw new AdminRuleViolationException("You cannot remove your own administrator role.");

        return await ChangeUserAsync(actor, userId, correlationId, ct, (user, activeAdmins, _) =>
        {
            if (user.IsAdmin && user.IsActive && newRole != UserRole.Admin && activeAdmins.Count <= 1)
                throw new AdminRuleViolationException("At least one active administrator must remain.");
            var from = AuthService.RoleName(user.Role);
            user.ChangeRole(newRole);
            return (AdminActionTypes.ChangeRole, $"{from} -> {AuthService.RoleName(newRole)}", revokeSessions: false);
        });
    }

    /// <summary>Every wallet, newest-id-last, optionally filtered by status (e.g. to review all frozen wallets).</summary>
    public async Task<WalletPage> ListWalletsAsync(Actor actor, string? status, int? limit, string? cursor, CancellationToken ct)
    {
        EnsureAdmin(actor);
        var take = PageSize(limit);

        WalletStatus? filter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            filter = status.Trim().ToLowerInvariant() switch
            {
                "active" => WalletStatus.Active,
                "frozen" => WalletStatus.Frozen,
                _ => throw new RequestValidationException("status must be 'active' or 'frozen'."),
            };
        }

        Guid? afterId = null;
        if (cursor is not null)
        {
            if (!Guid.TryParse(cursor, out var parsed))
                throw new RequestValidationException("cursor is invalid.");
            afterId = parsed;
        }

        var found = await ledger.ListWalletsAsync(filter, afterId, take + 1, ct);
        var items = found.Take(take).Select(w => new WalletResponse(
            w.Id, w.CustomerId, w.BalanceKobo, w.Currency, w.Status.ToString(), w.FrozenReason, w.CreatedAt)).ToList();
        return new WalletPage(items, found.Count > take ? items[^1].WalletId.ToString() : null);
    }

    /// <summary>Puts a debit hold on a wallet (e.g. fraud or dispute). Credits still land.</summary>
    public Task<WalletResponse> FreezeWalletAsync(
        Actor actor, Guid walletId, string? reason, string? correlationId, CancellationToken ct)
    {
        EnsureAdmin(actor);
        var why = Credentials.Reason(reason);
        return ChangeWalletAsync(actor, walletId, correlationId, ct, (wallet, now) =>
        {
            wallet.Freeze(why, now);
            return (AdminActionTypes.FreezeWallet, why);
        });
    }

    public Task<WalletResponse> UnfreezeWalletAsync(Actor actor, Guid walletId, string? correlationId, CancellationToken ct)
    {
        EnsureAdmin(actor);
        return ChangeWalletAsync(actor, walletId, correlationId, ct, (wallet, now) =>
        {
            wallet.Unfreeze(now);
            return (AdminActionTypes.UnfreezeWallet, null);
        });
    }

    public async Task<AdminActionPage> ListActionsAsync(Actor actor, int? limit, string? cursor, CancellationToken ct)
    {
        EnsureAdmin(actor);
        var take = PageSize(limit);
        long? before = null;
        if (cursor is not null)
        {
            if (!long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
                throw new RequestValidationException("cursor is invalid.");
            before = parsed;
        }

        var found = await users.ListAdminActionsAsync(before, take + 1, ct);
        var items = found.Take(take).Select(a => new AdminActionItem(
            a.Id, a.ActorId, a.Action, a.TargetType, a.TargetId, a.Detail, a.CorrelationId, a.OccurredAt)).ToList();
        return new AdminActionPage(items,
            found.Count > take ? items[^1].Id.ToString(CultureInfo.InvariantCulture) : null);
    }

    private async Task<UserProfile> ChangeUserAsync(
        Actor actor, Guid userId, string? correlationId, CancellationToken ct,
        Func<User, IReadOnlyList<Guid>, DateTimeOffset, (string Action, string? Detail, bool revokeSessions)> change)
    {
        await using var tx = await users.BeginAsync(ct);
        var now = timeProvider.GetLedgerNow();

        // Admin rows first (always in id order), then the target: every admin change takes locks in the same order.
        var activeAdmins = await users.LockActiveAdminIdsAsync(ct);
        var user = await users.LockByIdAsync(userId, ct) ?? throw new UserNotFoundException(userId);

        var (action, detail, revokeSessions) = change(user, activeAdmins, now);
        if (revokeSessions)
            await users.RevokeAllRefreshTokensAsync(user.Id, now, ct);

        users.Add(new AdminAction(actor.SubjectId, action, "user", user.SubjectId, detail, correlationId, now));
        await users.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        logger.LogInformation("Admin {AdminId} performed {Action} on user {UserId}", actor.UserId, action, user.Id);
        return AuthService.ToProfile(user, await users.FindWalletIdAsync(user.SubjectId, ct));
    }

    private async Task<WalletResponse> ChangeWalletAsync(
        Actor actor, Guid walletId, string? correlationId, CancellationToken ct,
        Func<Wallet, DateTimeOffset, (string Action, string? Detail)> change)
    {
        await using var tx = await ledger.BeginAsync(ct);
        var now = timeProvider.GetLedgerNow();

        // Same row lock transfers take, so a freeze can't interleave with a debit that's mid-flight.
        var wallet = await ledger.LockWalletAsync(walletId, ct) ?? throw new WalletNotFoundException(walletId);
        var (action, detail) = change(wallet, now);

        users.Add(new AdminAction(actor.SubjectId, action, "wallet", wallet.Id.ToString(), detail, correlationId, now));
        await ledger.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        logger.LogInformation("Admin {AdminId} performed {Action} on wallet {WalletId}", actor.UserId, action, wallet.Id);
        return new WalletResponse(wallet.Id, wallet.CustomerId, wallet.BalanceKobo, wallet.Currency,
            wallet.Status.ToString(), wallet.FrozenReason, wallet.CreatedAt);
    }

    private static void EnsureAdmin(Actor actor)
    {
        if (!actor.IsAdmin)
            throw new ForbiddenException("Administrator role required.");
    }

    private static int PageSize(int? limit)
    {
        var take = limit ?? 20;
        if (take is < 1 or > MaxPageSize)
            throw new RequestValidationException($"limit must be between 1 and {MaxPageSize}.");
        return take;
    }
}
