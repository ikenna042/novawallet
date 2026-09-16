using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using NovaWallet.Api.Auth;
using NovaWallet.Application;
using NovaWallet.IntegrationTests.Infrastructure;

namespace NovaWallet.IntegrationTests;

[Collection(LedgerCollection.Name)]
public sealed class AuthTests(LedgerApiFixture fixture)
{
    private readonly HttpClient _anonymous = fixture.Factory.CreateClient();

    private Task<HttpResponseMessage> Register(string email, string password = LedgerClient.DefaultPassword, object? extra = null) =>
        _anonymous.PostAsJsonAsync("/api/v1/auth/register", extra ?? new { email, password, fullName = "Ada Obi" });

    private Task<HttpResponseMessage> Login(string email, string password) =>
        _anonymous.PostAsJsonAsync("/api/v1/auth/login", new { email, password });

    private Task<HttpResponseMessage> Refresh(string refreshToken, HttpClient? http = null) =>
        (http ?? _anonymous).PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken });

    [Fact]
    public async Task Register_login_and_me_round_trip_as_a_customer()
    {
        var email = LedgerClient.NewEmail("ada");
        var registered = await Register(email);
        await registered.EnsureStatusAsync(HttpStatusCode.Created);
        var profile = (await registered.ReadDataAsync<UserProfile>());
        Assert.Equal(("customer", "Active", email), (profile.Role, profile.Status, profile.Email));

        var login = await Login(email.ToUpperInvariant(), LedgerClient.DefaultPassword); // emails are case-insensitive
        await login.EnsureStatusAsync(HttpStatusCode.OK);
        var tokens = (await login.ReadDataAsync<AuthTokens>());
        Assert.Equal("Bearer", tokens.TokenType);
        Assert.InRange(tokens.ExpiresIn, 1, 3600);
        Assert.False(string.IsNullOrEmpty(tokens.RefreshToken));

        var client = LedgerClient.For(fixture.Factory, tokens);
        var me = (await client.Http.GetDataAsync<UserProfile>("/api/v1/auth/me"))!;
        Assert.Equal(profile.UserId, me.UserId);
        Assert.Null(me.WalletId);

        var wallet = await client.CreateWalletAsync();
        me = (await client.Http.GetDataAsync<UserProfile>("/api/v1/auth/me"))!;
        Assert.Equal(wallet.WalletId, me.WalletId);
        Assert.NotNull(me.LastLoginAt);
    }

    [Fact]
    public async Task Public_registration_cannot_create_an_admin()
    {
        var sneaky = await Register("", extra: new
        {
            email = LedgerClient.NewEmail("sneaky"), password = LedgerClient.DefaultPassword, role = "admin",
        });
        Assert.Equal(HttpStatusCode.BadRequest, sneaky.StatusCode); // unknown field refused outright
    }

    [Fact]
    public async Task Duplicate_email_is_a_conflict_regardless_of_case()
    {
        var email = LedgerClient.NewEmail("dup");
        await (await Register(email)).EnsureStatusAsync(HttpStatusCode.Created);

        var again = await Register(email.ToUpperInvariant());
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("email_already_registered", (await again.ReadProblemAsync()).Code);
    }

    [Theory]
    [InlineData("Short1")]                  // too short
    [InlineData("onlylettersherenodigits")] // no digit
    [InlineData("12345678901234")]          // no letter
    public async Task Weak_passwords_are_rejected(string password)
    {
        var response = await Register(LedgerClient.NewEmail("weak"), password);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_error", (await response.ReadProblemAsync()).Code);
    }

    [Fact]
    public async Task Wrong_password_and_unknown_email_are_indistinguishable()
    {
        var email = LedgerClient.NewEmail("real");
        await (await Register(email)).EnsureStatusAsync(HttpStatusCode.Created);

        var wrongPassword = await Login(email, "Wrong-password-123");
        var unknownEmail = await Login(LedgerClient.NewEmail("ghost"), "Wrong-password-123");

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownEmail.StatusCode);
        var a = await wrongPassword.ReadProblemAsync();
        var b = await unknownEmail.ReadProblemAsync();
        Assert.Equal((a.Code, a.Title, a.Detail), (b.Code, b.Title, b.Detail));
        Assert.Equal("invalid_credentials", a.Code);
    }

    [Fact]
    public async Task Account_locks_after_five_failed_logins()
    {
        var email = LedgerClient.NewEmail("brute");
        await (await Register(email)).EnsureStatusAsync(HttpStatusCode.Created);

        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await Login(email, $"Guess-number-{i}")).StatusCode);

        // Even the right password is refused while locked, with the same generic answer.
        var locked = await Login(email, LedgerClient.DefaultPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        Assert.Equal("invalid_credentials", (await locked.ReadProblemAsync()).Code);
    }

    [Fact]
    public async Task Refresh_rotates_tokens_and_reusing_an_old_token_revokes_the_session()
    {
        var customer = await LedgerClient.CustomerAsync(fixture.Factory);
        var first = customer.Tokens.RefreshToken;

        var rotated = await Refresh(first);
        await rotated.EnsureStatusAsync(HttpStatusCode.OK);
        var second = (await rotated.ReadDataAsync<AuthTokens>());
        Assert.NotEqual(first, second.RefreshToken);

        var fresh = LedgerClient.For(fixture.Factory, second);
        await (await fresh.Http.GetAsync("/api/v1/auth/me")).EnsureStatusAsync(HttpStatusCode.OK);

        // Someone replays the first token (as a thief would): refused, and the whole family is revoked...
        var replay = await Refresh(first);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal("invalid_refresh_token", (await replay.ReadProblemAsync()).Code);

        // ...so the legitimate newer token no longer works either.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(second.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task Concurrent_refreshes_with_the_same_token_have_exactly_one_winner()
    {
        var customer = await LedgerClient.CustomerAsync(fixture.Factory);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 10)
            .Select(async _ => { await start.Task; return await Refresh(customer.Tokens.RefreshToken, fixture.Factory.CreateClient()); })
            .ToArray();
        start.SetResult();
        var responses = await Task.WhenAll(attempts);

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.OK),
            r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
    }

    [Fact]
    public async Task Logout_ends_the_session()
    {
        var customer = await LedgerClient.CustomerAsync(fixture.Factory);

        var logout = await customer.Http.PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = customer.Tokens.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        Assert.Null(await logout.ReadDataAsync<object>());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(customer.Tokens.RefreshToken)).StatusCode);

        // Logging out with someone else's (or a made-up) token is a silent no-op.
        var other = await LedgerClient.CustomerAsync(fixture.Factory);
        var foreign = await customer.Http.PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = other.Tokens.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, foreign.StatusCode);
        await (await Refresh(other.Tokens.RefreshToken)).EnsureStatusAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Correctly_signed_token_for_a_user_that_does_not_exist_is_rejected()
    {
        var issuer = fixture.Factory.Services.GetRequiredService<JwtTokenIssuer>();
        var token = issuer.Issue(Guid.NewGuid().ToString("N"), Roles.Admin, 1, TimeSpan.FromMinutes(5)).Token;
        var http = fixture.Factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/v1/admin/users")).StatusCode);
    }

    [Fact]
    public async Task Token_claiming_a_role_the_user_does_not_have_is_rejected()
    {
        var customer = await LedgerClient.CustomerAsync(fixture.Factory);
        var issuer = fixture.Factory.Services.GetRequiredService<JwtTokenIssuer>();
        var token = issuer.Issue(customer.Subject, Roles.Admin, 1, TimeSpan.FromMinutes(5)).Token;
        var http = fixture.Factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/v1/admin/users")).StatusCode);
    }

    [Fact]
    public async Task Sign_in_endpoints_are_rate_limited_per_client()
    {
        await using var limited = new LedgerApiFactory(fixture.ConnectionString, new Dictionary<string, string?>
        {
            ["RateLimiting:Auth:PermitLimit"] = "3",
            ["RateLimiting:Auth:WindowSeconds"] = "60",
        });
        var http = limited.CreateClient();

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await http.PostAsJsonAsync("/api/v1/auth/login", new { email = "x@example.test", password = "Nope-12345" })).StatusCode);

        var throttled = await http.PostAsJsonAsync("/api/v1/auth/login", new { email = "x@example.test", password = "Nope-12345" });
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.Equal("rate_limited", (await throttled.ReadProblemAsync()).Code);
    }
}
