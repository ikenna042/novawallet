using NovaWallet.Domain;

namespace NovaWallet.Application.Abstractions;

/// <summary>
/// Persistence port for users, sessions and the admin action log. Shares its unit of work with
/// <see cref="ILedgerStore"/>, so a single transaction can span both (e.g. freezing a wallet and logging it).
/// </summary>
public interface IUserStore
{
    Task<ILedgerTransactionScope> BeginAsync(CancellationToken ct);

    Task<User?> FindByIdAsync(Guid userId, CancellationToken ct);

    /// <summary>Loads the user with a row lock held until the transaction ends.</summary>
    Task<User?> LockByIdAsync(Guid userId, CancellationToken ct);

    /// <summary>Loads the user by (normalised) email with a row lock held until the transaction ends.</summary>
    Task<User?> LockByEmailAsync(string normalizedEmail, CancellationToken ct);

    /// <summary>Cheap, untracked read used on every authenticated request.</summary>
    Task<UserAuthState?> GetAuthStateAsync(Guid userId, CancellationToken ct);

    /// <summary>Returns false if the email is already registered.</summary>
    Task<bool> TryAddAsync(User user, CancellationToken ct);

    /// <summary>
    /// Locks every active admin row (in id order) and returns their ids. Serialises concurrent
    /// role/disable changes so two admins can't demote each other and leave nobody in charge.
    /// </summary>
    Task<IReadOnlyList<Guid>> LockActiveAdminIdsAsync(CancellationToken ct);

    /// <summary>Ordered by email; <paramref name="afterEmail"/> is the keyset cursor.</summary>
    Task<IReadOnlyList<User>> ListAsync(string? emailContains, string? afterEmail, int take, CancellationToken ct);

    Task<Guid?> FindWalletIdAsync(string customerId, CancellationToken ct);

    void Add(RefreshToken token);

    /// <summary>Loads the refresh token by hash with a row lock, so a token can only be rotated once.</summary>
    Task<RefreshToken?> LockRefreshTokenAsync(string tokenHash, CancellationToken ct);

    Task RevokeRefreshTokenFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken ct);

    Task RevokeAllRefreshTokensAsync(Guid userId, DateTimeOffset now, CancellationToken ct);

    void Add(AdminAction action);

    /// <summary>Newest first; <paramref name="beforeId"/> is the keyset cursor.</summary>
    Task<IReadOnlyList<AdminAction>> ListAdminActionsAsync(long? beforeId, int take, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}

public sealed record UserAuthState(UserStatus Status, UserRole Role, int TokenVersion);

public interface IPasswordHasher
{
    string Hash(string password);

    /// <param name="needsRehash">True when the hash uses outdated parameters and should be replaced.</param>
    bool Verify(string hash, string password, out bool needsRehash);
}

public interface ITokenIssuer
{
    IssuedAccessToken IssueAccessToken(User user);
}

public sealed record IssuedAccessToken(string Token, DateTimeOffset ExpiresAt);
