using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NovaWallet.Application;
using NovaWallet.Domain;

namespace NovaWallet.UnitTests;

public class CredentialsTests
{
    [Theory]
    [InlineData("  Ada.Obi@Example.COM ", "ada.obi@example.com")]
    [InlineData("x@y.ng", "x@y.ng")]
    public void Emails_are_trimmed_and_lower_cased(string input, string expected) =>
        Assert.Equal(expected, Credentials.Email(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-at-sign")]
    [InlineData("two@@example.com")]
    [InlineData("spaces in@example.com")]
    [InlineData("nodot@example")]
    public void Invalid_emails_are_rejected(string? email) =>
        Assert.Throws<RequestValidationException>(() => Credentials.Email(email));

    [Theory]
    [InlineData("Abcdefgh1")]            // 9 characters
    [InlineData("abcdefghijk")]          // no digit
    [InlineData("12345678901")]          // no letter
    [InlineData("Abcdefghi1\n")]         // control character
    public void Weak_or_malformed_passwords_are_rejected(string password) =>
        Assert.Throws<RequestValidationException>(() => Credentials.Password(password));

    [Fact]
    public void Ten_character_password_with_letter_and_digit_is_accepted() =>
        Assert.Equal("Abcdefghi1", Credentials.Password("Abcdefghi1"));

    [Fact]
    public void Refresh_tokens_are_random_and_only_their_hash_is_kept()
    {
        var (token1, hash1) = Credentials.NewRefreshToken();
        var (token2, _) = Credentials.NewRefreshToken();

        Assert.NotEqual(token1, token2);
        Assert.Equal(43, token1.Length); // 256 bits, base64url without padding
        Assert.DoesNotContain('+', token1);
        Assert.Equal(64, hash1.Length);
        Assert.Equal(hash1, Credentials.HashRefreshToken(token1));
        Assert.DoesNotContain(token1, hash1);
    }
}

public class UserAndWalletRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Fifth_failed_login_locks_the_account_for_fifteen_minutes()
    {
        var user = new User(Guid.NewGuid(), "a@b.ng", null, "h", UserRole.Customer, Now);
        for (var i = 0; i < 4; i++)
            user.RecordFailedLogin(Now);
        Assert.False(user.IsLockedOut(Now));

        user.RecordFailedLogin(Now);
        Assert.True(user.IsLockedOut(Now.AddMinutes(14)));
        Assert.False(user.IsLockedOut(Now.AddMinutes(15)));
    }

    [Fact]
    public void Successful_login_clears_the_failure_count()
    {
        var user = new User(Guid.NewGuid(), "a@b.ng", null, "h", UserRole.Customer, Now);
        for (var i = 0; i < 4; i++)
            user.RecordFailedLogin(Now);
        user.RecordSuccessfulLogin(Now);
        user.RecordFailedLogin(Now);
        Assert.False(user.IsLockedOut(Now));
    }

    [Fact]
    public void Disabling_or_changing_role_invalidates_existing_tokens()
    {
        var user = new User(Guid.NewGuid(), "a@b.ng", null, "h", UserRole.Customer, Now);
        Assert.Equal(1, user.TokenVersion);

        user.ChangeRole(UserRole.Customer); // no change, no bump
        Assert.Equal(1, user.TokenVersion);

        user.ChangeRole(UserRole.Admin);
        Assert.Equal(2, user.TokenVersion);

        user.Disable("fraud");
        Assert.Equal((3, UserStatus.Disabled), (user.TokenVersion, user.Status));
    }

    [Fact]
    public void Frozen_wallet_refuses_debits_but_accepts_credits()
    {
        var wallet = new Wallet(Guid.NewGuid(), "c", Now);
        wallet.Credit(Money.FromKobo(1_000), Now);
        wallet.Freeze("dispute", Now);

        Assert.Throws<WalletFrozenException>(() => wallet.Debit(Money.FromKobo(1), Now));
        wallet.Credit(Money.FromKobo(500), Now);
        Assert.Equal(1_500, wallet.BalanceKobo);

        wallet.Unfreeze(Now);
        wallet.Debit(Money.FromKobo(1), Now);
        Assert.Equal((WalletStatus.Active, (string?)null), (wallet.Status, wallet.FrozenReason));
    }
}

public class AuthServiceTests
{
    private const string Password = "Password-123";
    private readonly FakeUserStore _users = new();
    private readonly FakePasswordHasher _hasher = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero));
    private readonly AuthService _auth;

    public AuthServiceTests() =>
        _auth = new AuthService(_users, _hasher, new FakeTokenIssuer(_clock), Options.Create(new AuthOptions()), _clock,
            NullLogger<AuthService>.Instance);

    private Task<AuthTokens> Login(string email = "ada@example.ng", string password = Password) =>
        _auth.LoginAsync(new LoginCommand(email, password), CancellationToken.None);

    [Fact]
    public async Task Registration_creates_a_customer_with_a_hashed_password()
    {
        var profile = await _auth.RegisterAsync(new RegisterCommand(" Ada@Example.ng ", Password, "Ada"), CancellationToken.None);

        var stored = _users.Users[profile.UserId];
        Assert.Equal(("customer", "ada@example.ng"), (profile.Role, profile.Email));
        Assert.NotEqual(Password, stored.PasswordHash);
        await Assert.ThrowsAsync<EmailAlreadyRegisteredException>(() =>
            _auth.RegisterAsync(new RegisterCommand("ADA@example.ng", Password, null), CancellationToken.None));
    }

    [Fact]
    public async Task Unknown_email_still_runs_a_password_check()
    {
        await Assert.ThrowsAsync<InvalidCredentialsException>(() => Login("nobody@example.ng"));
        Assert.True(_hasher.Verifications >= 1);
    }

    [Fact]
    public async Task Disabled_user_cannot_sign_in_even_with_the_right_password()
    {
        _users.Seed("ada@example.ng", UserRole.Customer).Disable("fraud");
        await Assert.ThrowsAsync<InvalidCredentialsException>(() => Login());
    }

    [Fact]
    public async Task Lockout_expires_after_fifteen_minutes()
    {
        _users.Seed("ada@example.ng", UserRole.Customer);
        for (var i = 0; i < 5; i++)
            await Assert.ThrowsAsync<InvalidCredentialsException>(() => Login(password: "Wrong-123456"));

        await Assert.ThrowsAsync<InvalidCredentialsException>(() => Login());
        _clock.Advance(TimeSpan.FromMinutes(15));
        var tokens = await Login();
        Assert.NotNull(tokens.User.LastLoginAt);
    }

    [Fact]
    public async Task Refresh_rotates_within_the_same_family_and_reuse_revokes_it()
    {
        _users.Seed("ada@example.ng", UserRole.Customer);
        var first = await Login();

        var second = await _auth.RefreshAsync(first.RefreshToken, CancellationToken.None);
        var family = _users.RefreshTokens.Select(t => t.FamilyId).Distinct();
        Assert.Single(family);
        Assert.Equal(2, _users.RefreshTokens.Count);

        await Assert.ThrowsAsync<InvalidRefreshTokenException>(() => _auth.RefreshAsync(first.RefreshToken, CancellationToken.None));
        Assert.All(_users.RefreshTokens, t => Assert.NotNull(t.RevokedAt));
        await Assert.ThrowsAsync<InvalidRefreshTokenException>(() => _auth.RefreshAsync(second.RefreshToken, CancellationToken.None));
    }

    [Fact]
    public async Task Expired_refresh_token_is_rejected()
    {
        _users.Seed("ada@example.ng", UserRole.Customer);
        var tokens = await Login();
        _clock.Advance(TimeSpan.FromDays(7));
        await Assert.ThrowsAsync<InvalidRefreshTokenException>(() => _auth.RefreshAsync(tokens.RefreshToken, CancellationToken.None));
    }

    [Fact]
    public async Task Separate_logins_are_separate_sessions()
    {
        _users.Seed("ada@example.ng", UserRole.Customer);
        var phone = await Login();
        var laptop = await Login();

        var actor = new Actor(phone.User.UserId.ToString("N"), IsAdmin: false);
        await _auth.LogoutAsync(actor, phone.RefreshToken, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidRefreshTokenException>(() => _auth.RefreshAsync(phone.RefreshToken, CancellationToken.None));
        await _auth.RefreshAsync(laptop.RefreshToken, CancellationToken.None);
    }
}

public class AdminServiceTests
{
    private readonly FakeUserStore _users = new();
    private readonly AdminService _admin;
    private readonly User _root;

    public AdminServiceTests()
    {
        _admin = new AdminService(_users, new FakeLedgerStore(), new FakeTimeProvider(), NullLogger<AdminService>.Instance);
        _root = _users.Seed("root@example.ng", UserRole.Admin);
    }

    private Actor Root => new(_root.SubjectId, IsAdmin: true);

    [Fact]
    public async Task Customers_are_refused()
    {
        var customer = _users.Seed("c@example.ng", UserRole.Customer);
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            _admin.ListUsersAsync(new Actor(customer.SubjectId, IsAdmin: false), null, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task The_last_active_admin_cannot_be_disabled_or_demoted()
    {
        // Simulates a race where the caller's own admin rights were removed a moment ago,
        // leaving the target as the only active admin.
        var other = _users.Seed("other@example.ng", UserRole.Admin);
        _users.ActiveAdminsOverride = () => [other.Id];

        await Assert.ThrowsAsync<AdminRuleViolationException>(() =>
            _admin.DisableUserAsync(Root, other.Id, "reason", null, CancellationToken.None));
        await Assert.ThrowsAsync<AdminRuleViolationException>(() =>
            _admin.ChangeRoleAsync(Root, other.Id, "customer", null, CancellationToken.None));
        Assert.True(other.IsActive && other.IsAdmin);
        Assert.Empty(_users.AdminActions);
    }

    [Fact]
    public async Task Disabling_revokes_sessions_and_is_logged()
    {
        var customer = _users.Seed("c@example.ng", UserRole.Customer);
        _users.Add(new RefreshToken(Guid.NewGuid(), customer.Id, Guid.NewGuid(), "h", DateTimeOffset.UnixEpoch,
            DateTimeOffset.MaxValue));

        var profile = await _admin.DisableUserAsync(Root, customer.Id, "  fraud  ", "corr-9", CancellationToken.None);

        Assert.Equal("Disabled", profile.Status);
        Assert.All(_users.RefreshTokens, t => Assert.NotNull(t.RevokedAt));
        var action = Assert.Single(_users.AdminActions);
        Assert.Equal((AdminActionTypes.DisableUser, "fraud", "corr-9", _root.SubjectId),
            (action.Action, action.Detail, action.CorrelationId, action.ActorId));
    }

    [Fact]
    public async Task Role_names_are_validated()
    {
        var customer = _users.Seed("c@example.ng", UserRole.Customer);
        await Assert.ThrowsAsync<RequestValidationException>(() =>
            _admin.ChangeRoleAsync(Root, customer.Id, "superuser", null, CancellationToken.None));
        await Assert.ThrowsAsync<UserNotFoundException>(() =>
            _admin.ChangeRoleAsync(Root, Guid.NewGuid(), "admin", null, CancellationToken.None));
    }
}
