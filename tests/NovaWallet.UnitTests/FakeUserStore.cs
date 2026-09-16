using NovaWallet.Application.Abstractions;
using NovaWallet.Domain;

namespace NovaWallet.UnitTests;

/// <summary>Single-threaded in-memory <see cref="IUserStore"/>; locking is a no-op here (covered against PostgreSQL).</summary>
internal sealed class FakeUserStore : IUserStore
{
    public Dictionary<Guid, User> Users { get; } = [];
    public List<RefreshToken> RefreshTokens { get; } = [];
    public List<AdminAction> AdminActions { get; } = [];
    public Dictionary<string, Guid> WalletsByCustomer { get; } = [];

    /// <summary>Lets a test pretend the active-admin set is different from what's stored (e.g. a race).</summary>
    public Func<IReadOnlyList<Guid>>? ActiveAdminsOverride { get; set; }

    public User Seed(string email, UserRole role, string passwordHash = "hash:Password-123")
    {
        var user = new User(Guid.NewGuid(), email, null, passwordHash, role, DateTimeOffset.UnixEpoch);
        Users[user.Id] = user;
        return user;
    }

    public Task<ILedgerTransactionScope> BeginAsync(CancellationToken ct) => Task.FromResult<ILedgerTransactionScope>(new NoopScope());

    public Task<User?> FindByIdAsync(Guid userId, CancellationToken ct) => Task.FromResult(Users.GetValueOrDefault(userId));

    public Task<User?> LockByIdAsync(Guid userId, CancellationToken ct) => FindByIdAsync(userId, ct);

    public Task<User?> LockByEmailAsync(string normalizedEmail, CancellationToken ct) =>
        Task.FromResult(Users.Values.SingleOrDefault(u => u.Email == normalizedEmail));

    public Task<UserAuthState?> GetAuthStateAsync(Guid userId, CancellationToken ct) =>
        Task.FromResult(Users.TryGetValue(userId, out var u) ? new UserAuthState(u.Status, u.Role, u.TokenVersion) : null);

    public Task<bool> TryAddAsync(User user, CancellationToken ct)
    {
        if (Users.Values.Any(u => u.Email == user.Email))
            return Task.FromResult(false);
        Users[user.Id] = user;
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<Guid>> LockActiveAdminIdsAsync(CancellationToken ct) =>
        Task.FromResult(ActiveAdminsOverride?.Invoke()
                        ?? Users.Values.Where(u => u.IsAdmin && u.IsActive).Select(u => u.Id).Order().ToList());

    public Task<IReadOnlyList<User>> ListAsync(string? emailContains, string? afterEmail, int take, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<User>>(Users.Values
            .Where(u => emailContains is null || u.Email.Contains(emailContains))
            .Where(u => afterEmail is null || string.CompareOrdinal(u.Email, afterEmail) > 0)
            .OrderBy(u => u.Email, StringComparer.Ordinal).Take(take).ToList());

    public Task<Guid?> FindWalletIdAsync(string customerId, CancellationToken ct) =>
        Task.FromResult(WalletsByCustomer.TryGetValue(customerId, out var id) ? id : (Guid?)null);

    public void Add(RefreshToken token) => RefreshTokens.Add(token);

    public Task<RefreshToken?> LockRefreshTokenAsync(string tokenHash, CancellationToken ct) =>
        Task.FromResult(RefreshTokens.SingleOrDefault(t => t.TokenHash == tokenHash));

    public Task RevokeRefreshTokenFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var t in RefreshTokens.Where(t => t.FamilyId == familyId))
            t.Revoke(now);
        return Task.CompletedTask;
    }

    public Task RevokeAllRefreshTokensAsync(Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var t in RefreshTokens.Where(t => t.UserId == userId))
            t.Revoke(now);
        return Task.CompletedTask;
    }

    public void Add(AdminAction action) => AdminActions.Add(action);

    public Task<IReadOnlyList<AdminAction>> ListAdminActionsAsync(long? beforeId, int take, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AdminAction>>(AdminActions.AsEnumerable().Reverse().Take(take).ToList());

    public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;

    private sealed class NoopScope : ILedgerTransactionScope
    {
        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>Deterministic stand-ins for the password hasher and token issuer.</summary>
internal sealed class FakePasswordHasher : IPasswordHasher
{
    public int Verifications { get; private set; }

    public string Hash(string password) => "hash:" + password;

    public bool Verify(string hash, string password, out bool needsRehash)
    {
        Verifications++;
        needsRehash = false;
        return hash == "hash:" + password;
    }
}

internal sealed class FakeTokenIssuer(TimeProvider clock) : ITokenIssuer
{
    public IssuedAccessToken IssueAccessToken(User user) =>
        new($"access:{user.Id:N}:{user.TokenVersion}:{user.Role}", clock.GetUtcNow().AddMinutes(15));
}
