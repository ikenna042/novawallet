namespace NovaWallet.Domain;

public enum UserRole
{
    Customer = 1,
    Admin = 2,
}

public enum UserStatus
{
    Active = 1,
    Disabled = 2,
}

/// <summary>
/// A person who can sign in. Deliberately minimal (NDPA data minimisation): an email to sign in with and an
/// optional display name; KYC identifiers such as BVN/NIN belong to the customer service, not the ledger.
/// </summary>
public sealed class User
{
    public const int MaxFailedLogins = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private User() { } // EF Core

    public User(Guid id, string email, string? fullName, string passwordHash, UserRole role, DateTimeOffset createdAt)
    {
        Id = id;
        Email = NormalizeEmail(email);
        FullName = fullName;
        PasswordHash = passwordHash;
        Role = role;
        Status = UserStatus.Active;
        TokenVersion = 1;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public string Email { get; private set; } = null!;
    public string? FullName { get; private set; }
    public string PasswordHash { get; private set; } = null!;
    public UserRole Role { get; private set; }
    public UserStatus Status { get; private set; }

    /// <summary>
    /// Copied into every access token as the <c>ver</c> claim. Incrementing it invalidates all tokens issued
    /// before, which is how disabling a user or changing their role takes effect immediately.
    /// </summary>
    public int TokenVersion { get; private set; }

    public int FailedLoginCount { get; private set; }
    public DateTimeOffset? LockedUntil { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? LastLoginAt { get; private set; }
    public string? DisabledReason { get; private set; }

    /// <summary>The identifier used as the JWT subject and as the wallet's customer id.</summary>
    public string SubjectId => SubjectFor(Id);

    public bool IsActive => Status == UserStatus.Active;
    public bool IsAdmin => Role == UserRole.Admin;

    public static string SubjectFor(Guid userId) => userId.ToString("N");

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public bool IsLockedOut(DateTimeOffset now) => LockedUntil is { } until && until > now;

    public void RecordFailedLogin(DateTimeOffset now)
    {
        FailedLoginCount++;
        if (FailedLoginCount >= MaxFailedLogins)
        {
            LockedUntil = now + LockoutDuration;
            FailedLoginCount = 0;
        }
    }

    public void RecordSuccessfulLogin(DateTimeOffset now)
    {
        FailedLoginCount = 0;
        LockedUntil = null;
        LastLoginAt = now;
    }

    public void ChangePasswordHash(string passwordHash) => PasswordHash = passwordHash;

    public void Disable(string reason)
    {
        Status = UserStatus.Disabled;
        DisabledReason = reason;
        TokenVersion++;
    }

    public void Enable()
    {
        Status = UserStatus.Active;
        DisabledReason = null;
        FailedLoginCount = 0;
        LockedUntil = null;
    }

    public void ChangeRole(UserRole role)
    {
        if (Role == role)
            return;
        Role = role;
        TokenVersion++;
    }
}

/// <summary>
/// A long-lived credential used only to obtain new access tokens. Only a SHA-256 hash is stored, so a database
/// leak does not leak usable tokens. Tokens rotate on every use; all tokens descended from one login share a
/// <see cref="FamilyId"/>, so reuse of an old token (a sign of theft) can revoke the whole chain.
/// </summary>
public sealed class RefreshToken
{
    private RefreshToken() { } // EF Core

    public RefreshToken(Guid id, Guid userId, Guid familyId, string tokenHash, DateTimeOffset createdAt, DateTimeOffset expiresAt)
    {
        Id = id;
        UserId = userId;
        FamilyId = familyId;
        TokenHash = tokenHash;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid FamilyId { get; private set; }
    public string TokenHash { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public Guid? ReplacedById { get; private set; }

    public bool IsUsable(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;

    public void Revoke(DateTimeOffset now, Guid? replacedBy = null)
    {
        RevokedAt ??= now;
        ReplacedById ??= replacedBy;
    }
}

public static class AdminActionTypes
{
    public const string DisableUser = "USER_DISABLED";
    public const string EnableUser = "USER_ENABLED";
    public const string ChangeRole = "USER_ROLE_CHANGED";
    public const string FreezeWallet = "WALLET_FROZEN";
    public const string UnfreezeWallet = "WALLET_UNFROZEN";
    public const string CreateAdmin = "ADMIN_SEEDED";
}

/// <summary>Append-only record of an administrative change (who did what to whom, and why).</summary>
public sealed class AdminAction
{
    private AdminAction() { } // EF Core

    public AdminAction(
        string actorId, string action, string targetType, string targetId, string? detail, string? correlationId,
        DateTimeOffset occurredAt)
    {
        ActorId = actorId;
        Action = action;
        TargetType = targetType;
        TargetId = targetId;
        Detail = detail;
        CorrelationId = correlationId;
        OccurredAt = occurredAt;
    }

    public long Id { get; private set; }
    public string ActorId { get; private set; } = null!;
    public string Action { get; private set; } = null!;
    public string TargetType { get; private set; } = null!;
    public string TargetId { get; private set; } = null!;
    public string? Detail { get; private set; }
    public string? CorrelationId { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
}
