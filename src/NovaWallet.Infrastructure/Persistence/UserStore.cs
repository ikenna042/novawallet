using Microsoft.EntityFrameworkCore;
using Npgsql;
using NovaWallet.Application.Abstractions;
using NovaWallet.Domain;

namespace NovaWallet.Infrastructure.Persistence;

public sealed class UserStore(LedgerDbContext db) : IUserStore
{
    public Task<ILedgerTransactionScope> BeginAsync(CancellationToken ct) => DbTransactionScope.BeginAsync(db, ct);

    public Task<User?> FindByIdAsync(Guid userId, CancellationToken ct) =>
        db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct);

    public async Task<User?> LockByIdAsync(Guid userId, CancellationToken ct)
    {
        EnsureNotTracked(userId);
        var rows = await db.Users.FromSql($"SELECT * FROM users WHERE id = {userId} FOR UPDATE").ToListAsync(ct);
        return rows.SingleOrDefault();
    }

    public async Task<User?> LockByEmailAsync(string normalizedEmail, CancellationToken ct)
    {
        var rows = await db.Users.FromSql($"SELECT * FROM users WHERE email = {normalizedEmail} FOR UPDATE").ToListAsync(ct);
        return rows.SingleOrDefault();
    }

    public Task<UserAuthState?> GetAuthStateAsync(Guid userId, CancellationToken ct) =>
        db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new UserAuthState(u.Status, u.Role, u.TokenVersion))
            .SingleOrDefaultAsync(ct);

    public async Task<bool> TryAddAsync(User user, CancellationToken ct)
    {
        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
                                           {
                                               SqlState: PostgresErrorCodes.UniqueViolation,
                                               ConstraintName: LedgerDbContext.UserEmailIndex,
                                           })
        {
            db.Entry(user).State = EntityState.Detached;
            return false;
        }
    }

    public async Task<IReadOnlyList<Guid>> LockActiveAdminIdsAsync(CancellationToken ct) =>
        await db.Database
            .SqlQuery<Guid>($"""
                             SELECT id AS "Value" FROM users
                             WHERE role = 'Admin' AND status = 'Active'
                             ORDER BY id
                             FOR UPDATE
                             """)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<User>> ListAsync(string? emailContains, string? afterEmail, int take, CancellationToken ct)
    {
        var query = db.Users.AsNoTracking();
        if (emailContains is not null)
            query = query.Where(u => u.Email.Contains(emailContains));
        if (afterEmail is not null)
            query = query.Where(u => string.Compare(u.Email, afterEmail) > 0);
        return await query.OrderBy(u => u.Email).Take(take).ToListAsync(ct);
    }

    public Task<Guid?> FindWalletIdAsync(string customerId, CancellationToken ct) =>
        db.Wallets.AsNoTracking()
            .Where(w => w.CustomerId == customerId)
            .Select(w => (Guid?)w.Id)
            .SingleOrDefaultAsync(ct);

    public void Add(RefreshToken token) => db.RefreshTokens.Add(token);

    public async Task<RefreshToken?> LockRefreshTokenAsync(string tokenHash, CancellationToken ct)
    {
        var rows = await db.RefreshTokens
            .FromSql($"SELECT * FROM refresh_tokens WHERE token_hash = {tokenHash} FOR UPDATE")
            .ToListAsync(ct);
        return rows.SingleOrDefault();
    }

    public Task RevokeRefreshTokenFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken ct) =>
        db.RefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.RevokedAt, now), ct);

    public Task RevokeAllRefreshTokensAsync(Guid userId, DateTimeOffset now, CancellationToken ct) =>
        db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.RevokedAt, now), ct);

    public void Add(AdminAction action) => db.AdminActions.Add(action);

    public async Task<IReadOnlyList<AdminAction>> ListAdminActionsAsync(long? beforeId, int take, CancellationToken ct)
    {
        var query = db.AdminActions.AsNoTracking();
        if (beforeId is { } before)
            query = query.Where(a => a.Id < before);
        return await query.OrderByDescending(a => a.Id).Take(take).ToListAsync(ct);
    }

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);

    // As with wallets: EF would hand back the stale tracked instance instead of the freshly locked row.
    private void EnsureNotTracked(Guid userId)
    {
        if (db.ChangeTracker.Entries<User>().Any(e => e.Entity.Id == userId))
            throw new InvalidOperationException($"User {userId} was loaded before it was locked.");
    }
}
