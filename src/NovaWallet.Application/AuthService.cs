using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NovaWallet.Application.Abstractions;
using NovaWallet.Domain;

namespace NovaWallet.Application;

public sealed class AuthService(
    IUserStore users,
    IPasswordHasher hasher,
    ITokenIssuer tokens,
    IOptions<AuthOptions> options,
    TimeProvider timeProvider,
    ILogger<AuthService> logger)
{
    // Verified against when the email is unknown, so "no such user" takes as long as "wrong password".
    // Computed once per process (a benign race at most computes it twice).
    private static string? s_dummyHash;

    private string DummyHash => s_dummyHash ??= hasher.Hash("not-a-real-password-0");

    /// <summary>Public sign-up. Always creates a customer; admins are only created by other admins or seeding.</summary>
    public async Task<UserProfile> RegisterAsync(RegisterCommand command, CancellationToken ct)
    {
        var email = Credentials.Email(command.Email);
        var password = Credentials.Password(command.Password);
        var fullName = Credentials.FullName(command.FullName);

        var user = new User(Guid.NewGuid(), email, fullName, hasher.Hash(password), UserRole.Customer,
            timeProvider.GetLedgerNow());
        if (!await users.TryAddAsync(user, ct))
            throw new EmailAlreadyRegisteredException();

        logger.LogInformation("User {UserId} registered", user.Id);
        return ToProfile(user, walletId: null);
    }

    public async Task<AuthTokens> LoginAsync(LoginCommand command, CancellationToken ct)
    {
        // Malformed input gets the same answer as a wrong password.
        if (string.IsNullOrWhiteSpace(command.Email) || string.IsNullOrEmpty(command.Password)
            || command.Password.Length > Credentials.MaxPasswordLength)
            throw new InvalidCredentialsException();

        await using var tx = await users.BeginAsync(ct);
        var now = timeProvider.GetLedgerNow();

        // Row lock so concurrent attempts can't race past the failed-login counter.
        var user = await users.LockByEmailAsync(User.NormalizeEmail(command.Email), ct);
        if (user is null)
        {
            hasher.Verify(DummyHash, command.Password, out _);
            throw new InvalidCredentialsException();
        }

        var passwordOk = hasher.Verify(user.PasswordHash, command.Password, out var needsRehash);

        if (!user.IsActive || user.IsLockedOut(now))
        {
            logger.LogWarning("Login refused for {UserId}: account disabled or locked", user.Id);
            throw new InvalidCredentialsException();
        }

        if (!passwordOk)
        {
            user.RecordFailedLogin(now);
            await users.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            logger.LogWarning("Failed login for {UserId}", user.Id);
            throw new InvalidCredentialsException();
        }

        user.RecordSuccessfulLogin(now);
        if (needsRehash)
            user.ChangePasswordHash(hasher.Hash(command.Password));

        var result = await IssueAsync(user, Guid.NewGuid(), now, ct);
        await users.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        logger.LogInformation("User {UserId} signed in", user.Id);
        return result;
    }

    /// <summary>
    /// Exchanges a refresh token for a new pair and revokes the old one (rotation). Presenting a token that was
    /// already used means it was copied: the whole session family is revoked and the user must sign in again.
    /// </summary>
    public async Task<AuthTokens> RefreshAsync(string? refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(refreshToken) || refreshToken.Length > 128)
            throw new InvalidRefreshTokenException();

        await using var tx = await users.BeginAsync(ct);
        var now = timeProvider.GetLedgerNow();

        var current = await users.LockRefreshTokenAsync(Credentials.HashRefreshToken(refreshToken), ct)
                      ?? throw new InvalidRefreshTokenException();

        if (current.RevokedAt is not null)
        {
            await users.RevokeRefreshTokenFamilyAsync(current.FamilyId, now, ct);
            await tx.CommitAsync(ct);
            logger.LogWarning("Refresh token reuse detected for user {UserId}; session family {FamilyId} revoked",
                current.UserId, current.FamilyId);
            throw new InvalidRefreshTokenException();
        }

        if (!current.IsUsable(now))
            throw new InvalidRefreshTokenException();

        var user = await users.FindByIdAsync(current.UserId, ct);
        if (user is null || !user.IsActive)
        {
            await users.RevokeRefreshTokenFamilyAsync(current.FamilyId, now, ct);
            await tx.CommitAsync(ct);
            throw new InvalidRefreshTokenException();
        }

        var result = await IssueAsync(user, current.FamilyId, now, ct, rotated: current);
        await users.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return result;
    }

    /// <summary>Ends the session the refresh token belongs to. Always succeeds, so tokens can't be probed.</summary>
    public async Task LogoutAsync(Actor actor, string? refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(refreshToken) || refreshToken.Length > 128)
            return;

        await using var tx = await users.BeginAsync(ct);
        var token = await users.LockRefreshTokenAsync(Credentials.HashRefreshToken(refreshToken), ct);
        if (token is null || token.UserId != actor.UserId)
            return;

        await users.RevokeRefreshTokenFamilyAsync(token.FamilyId, timeProvider.GetLedgerNow(), ct);
        await tx.CommitAsync(ct);
        logger.LogInformation("User {UserId} signed out", actor.UserId);
    }

    public async Task<UserProfile> MeAsync(Actor actor, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(actor.UserId, ct) ?? throw new UserNotFoundException(actor.UserId);
        return ToProfile(user, await users.FindWalletIdAsync(user.SubjectId, ct));
    }

    private async Task<AuthTokens> IssueAsync(
        User user, Guid familyId, DateTimeOffset now, CancellationToken ct, RefreshToken? rotated = null)
    {
        var access = tokens.IssueAccessToken(user);
        var (refresh, hash) = Credentials.NewRefreshToken();
        var entity = new RefreshToken(Guid.NewGuid(), user.Id, familyId, hash, now,
            now.AddDays(options.Value.RefreshTokenDays));
        users.Add(entity);
        rotated?.Revoke(now, replacedBy: entity.Id);

        var expiresIn = (int)Math.Max(0, (access.ExpiresAt - now).TotalSeconds);
        return new AuthTokens(access.Token, "Bearer", expiresIn, access.ExpiresAt, refresh, entity.ExpiresAt,
            ToProfile(user, await users.FindWalletIdAsync(user.SubjectId, ct)));
    }

    internal static UserProfile ToProfile(User u, Guid? walletId) => new(
        u.Id, u.Email, u.FullName, RoleName(u.Role), u.Status.ToString(), u.DisabledReason, walletId,
        u.CreatedAt, u.LastLoginAt);

    public static string RoleName(UserRole role) => role == UserRole.Admin ? "admin" : "customer";
}
