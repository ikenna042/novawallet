using System.Net;
using System.Net.Http.Json;
using NovaWallet.Application;
using NovaWallet.IntegrationTests.Infrastructure;
using Npgsql;

namespace NovaWallet.IntegrationTests;

[Collection(LedgerCollection.Name)]
public sealed class AdminTests(LedgerApiFixture fixture)
{
    private LedgerClient Admin => fixture.Admin;

    private async Task<(LedgerClient Client, Guid WalletId)> FundedCustomerAsync(long balanceKobo)
    {
        var client = await LedgerClient.CustomerAsync(fixture.Factory);
        var wallet = await client.CreateWalletAsync();
        if (balanceKobo > 0)
            await (await Admin.CreditAsync(wallet.WalletId, balanceKobo)).EnsureStatusAsync(HttpStatusCode.Created);
        return (client, wallet.WalletId);
    }

    private Task<HttpResponseMessage> Post(LedgerClient caller, string path, object? body = null) =>
        caller.Http.PostAsJsonAsync(path, body ?? new { });

    [Theory]
    [InlineData("GET", "/api/v1/admin/users")]
    [InlineData("GET", "/api/v1/admin/actions")]
    [InlineData("POST", "/api/v1/admin/users/{self}/role")]
    [InlineData("POST", "/api/v1/admin/wallets/{wallet}/freeze")]
    public async Task Customers_and_anonymous_callers_cannot_use_admin_endpoints(string method, string path)
    {
        var (customer, wallet) = await FundedCustomerAsync(0);
        path = path.Replace("{self}", customer.UserId.ToString()).Replace("{wallet}", wallet.ToString());
        HttpRequestMessage Request() => new(new HttpMethod(method), path)
        {
            Content = method == "POST" ? JsonContent.Create(new { role = "admin", reason = "try" }) : null,
        };

        Assert.Equal(HttpStatusCode.Forbidden, (await customer.Http.SendAsync(Request())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Factory.CreateClient().SendAsync(Request())).StatusCode);
    }

    [Fact]
    public async Task Admin_can_find_a_user_and_view_their_wallet()
    {
        var (customer, wallet) = await FundedCustomerAsync(7_500_00);

        var page = (await Admin.Http.GetFromJsonAsync<UserPage>($"/api/v1/admin/users?email={customer.Email}"))!;
        var found = Assert.Single(page.Items);
        Assert.Equal((customer.UserId, wallet), (found.UserId, found.WalletId!.Value));

        var byId = (await Admin.Http.GetFromJsonAsync<UserProfile>($"/api/v1/admin/users/{customer.UserId}"))!;
        Assert.Equal("customer", byId.Role);

        Assert.Equal(7_500_00, await Admin.GetBalanceAsync(wallet));
        Assert.Single((await Admin.GetStatementAsync(wallet)).Items);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin.Http.GetAsync($"/api/v1/admin/users/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task User_list_is_paginated_by_email()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        for (var i = 0; i < 3; i++)
            await LedgerClient.CustomerAsync(fixture.Factory, email: $"page{i}-{tag}@example.test");

        var first = (await Admin.Http.GetFromJsonAsync<UserPage>($"/api/v1/admin/users?email={tag}&limit=2"))!;
        Assert.Equal(2, first.Items.Count);
        Assert.NotNull(first.NextCursor);
        var second = (await Admin.Http.GetFromJsonAsync<UserPage>(
            $"/api/v1/admin/users?email={tag}&limit=2&cursor={Uri.EscapeDataString(first.NextCursor!)}"))!;
        Assert.Single(second.Items);
        Assert.Null(second.NextCursor);
        Assert.Equal(new[] { $"page0-{tag}@example.test", $"page1-{tag}@example.test", $"page2-{tag}@example.test" },
            first.Items.Concat(second.Items).Select(u => u.Email));
    }

    [Fact]
    public async Task Disabling_a_user_cuts_off_access_immediately_and_enabling_restores_it()
    {
        var (customer, wallet) = await FundedCustomerAsync(1_000_00);
        await (await customer.Http.GetAsync("/api/v1/auth/me")).EnsureStatusAsync(HttpStatusCode.OK);

        var disabled = await Post(Admin, $"/api/v1/admin/users/{customer.UserId}/disable", new { reason = "Suspected account takeover" });
        await disabled.EnsureStatusAsync(HttpStatusCode.OK);
        Assert.Equal("Disabled", (await disabled.Content.ReadFromJsonAsync<UserProfile>())!.Status);

        // The access token is still unexpired, but it no longer works...
        Assert.Equal(HttpStatusCode.Unauthorized, (await customer.Http.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await customer.TransferAsync(wallet, (await FundedCustomerAsync(0)).WalletId, 1_00, Guid.NewGuid().ToString())).StatusCode);
        // ...nor does the refresh token, nor signing in again.
        var anonymous = fixture.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = customer.Tokens.RefreshToken })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/api/v1/auth/login", new { email = customer.Email, password = LedgerClient.DefaultPassword })).StatusCode);

        await (await Post(Admin, $"/api/v1/admin/users/{customer.UserId}/enable")).EnsureStatusAsync(HttpStatusCode.OK);
        var again = await LedgerClient.LoginAsync(fixture.Factory, customer.Email, LedgerClient.DefaultPassword);
        Assert.Equal(1_000_00, await again.GetBalanceAsync(wallet));
    }

    [Fact]
    public async Task Promotion_invalidates_old_tokens_and_takes_effect_on_next_sign_in()
    {
        var customer = await LedgerClient.CustomerAsync(fixture.Factory);

        var promoted = await Post(Admin, $"/api/v1/admin/users/{customer.UserId}/role", new { role = "admin" });
        await promoted.EnsureStatusAsync(HttpStatusCode.OK);
        Assert.Equal("admin", (await promoted.Content.ReadFromJsonAsync<UserProfile>())!.Role);

        Assert.Equal(HttpStatusCode.Unauthorized, (await customer.Http.GetAsync("/api/v1/auth/me")).StatusCode);

        var asAdmin = await LedgerClient.LoginAsync(fixture.Factory, customer.Email, LedgerClient.DefaultPassword);
        Assert.Equal("admin", asAdmin.Tokens.User.Role);
        await (await asAdmin.Http.GetAsync("/api/v1/admin/users")).EnsureStatusAsync(HttpStatusCode.OK);

        // An admin can't demote themselves; another admin has to do it.
        var selfDemote = await Post(asAdmin, $"/api/v1/admin/users/{asAdmin.UserId}/role", new { role = "customer" });
        Assert.Equal(HttpStatusCode.Conflict, selfDemote.StatusCode);
        Assert.Equal("admin_rule_violation", (await selfDemote.ReadProblemAsync()).Code);

        await (await Post(Admin, $"/api/v1/admin/users/{asAdmin.UserId}/role", new { role = "customer" }))
            .EnsureStatusAsync(HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.Unauthorized, (await asAdmin.Http.GetAsync("/api/v1/admin/users")).StatusCode);
    }

    [Fact]
    public async Task Admin_cannot_disable_themselves()
    {
        var response = await Post(Admin, $"/api/v1/admin/users/{Admin.UserId}/disable", new { reason = "oops" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("admin_rule_violation", (await response.ReadProblemAsync()).Code);
    }

    [Theory]
    [InlineData("disable", """{"reason":""}""")]
    [InlineData("role", """{"role":"superuser"}""")]
    public async Task Admin_requests_are_validated(string action, string body)
    {
        var customer = await LedgerClient.CustomerAsync(fixture.Factory);
        var response = await Admin.Http.PostAsync($"/api/v1/admin/users/{customer.UserId}/{action}",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Frozen_wallet_cannot_send_but_can_receive_until_unfrozen()
    {
        var (alice, aliceWallet) = await FundedCustomerAsync(10_000_00);
        var (bob, bobWallet) = await FundedCustomerAsync(10_000_00);

        var frozen = await Post(Admin, $"/api/v1/admin/wallets/{aliceWallet}/freeze", new { reason = "Chargeback dispute #4471" });
        await frozen.EnsureStatusAsync(HttpStatusCode.OK);
        var state = (await frozen.Content.ReadFromJsonAsync<WalletResponse>())!;
        Assert.Equal(("Frozen", "Chargeback dispute #4471"), (state.Status, state.FrozenReason));

        var outbound = await alice.TransferAsync(aliceWallet, bobWallet, 1_00, Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, outbound.StatusCode);
        Assert.Equal("wallet_frozen", (await outbound.ReadProblemAsync()).Code);

        await (await bob.TransferAsync(bobWallet, aliceWallet, 2_00, Guid.NewGuid().ToString())).EnsureStatusAsync(HttpStatusCode.Created);
        await (await Admin.CreditAsync(aliceWallet, 3_00)).EnsureStatusAsync(HttpStatusCode.Created);
        Assert.Equal(10_005_00, await alice.GetBalanceAsync(aliceWallet));
        Assert.Equal("Frozen", (await alice.Http.GetFromJsonAsync<WalletResponse>($"/api/v1/wallets/{aliceWallet}"))!.Status);

        await (await Post(Admin, $"/api/v1/admin/wallets/{aliceWallet}/unfreeze")).EnsureStatusAsync(HttpStatusCode.OK);
        await (await alice.TransferAsync(aliceWallet, bobWallet, 1_00, Guid.NewGuid().ToString())).EnsureStatusAsync(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Freeze_requires_a_reason_and_an_existing_wallet()
    {
        var (_, wallet) = await FundedCustomerAsync(0);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(Admin, $"/api/v1/admin/wallets/{wallet}/freeze", new { reason = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(Admin, $"/api/v1/admin/wallets/{Guid.NewGuid()}/freeze", new { reason = "x" })).StatusCode);
    }

    [Fact]
    public async Task Admin_actions_are_logged_and_the_log_is_append_only()
    {
        var (customer, wallet) = await FundedCustomerAsync(0);
        await (await Post(Admin, $"/api/v1/admin/wallets/{wallet}/freeze", new { reason = "KYC review" })).EnsureStatusAsync(HttpStatusCode.OK);
        await (await Post(Admin, $"/api/v1/admin/users/{customer.UserId}/disable", new { reason = "KYC review" })).EnsureStatusAsync(HttpStatusCode.OK);

        var log = (await Admin.Http.GetFromJsonAsync<AdminActionPage>("/api/v1/admin/actions?limit=50"))!;
        var mine = log.Items.Where(a => a.TargetId == wallet.ToString() || a.TargetId == customer.Subject).ToList();
        Assert.Equal(new[] { "USER_DISABLED", "WALLET_FROZEN" }, mine.Select(a => a.Action));
        Assert.All(mine, a => Assert.Equal((Admin.Subject, "KYC review"), (a.ActorId, a.Detail)));

        await using var db = await fixture.OpenConnectionAsync();
        foreach (var sql in new[] { "UPDATE admin_actions SET detail = 'nothing to see'", "DELETE FROM admin_actions" })
        {
            await using var cmd = new NpgsqlCommand(sql, db);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Contains("append-only", ex.MessageText);
        }
    }

    [Fact]
    public async Task Admin_seeding_is_idempotent_across_restarts()
    {
        await using (var restarted = new LedgerApiFactory(fixture.ConnectionString))
        {
            _ = restarted.Server;
            await LedgerClient.AdminAsync(restarted);
        }

        await using var db = await fixture.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM users WHERE email = @e AND role = 'Admin'", db);
        cmd.Parameters.AddWithValue("e", LedgerApiFixture.AdminEmail);
        Assert.Equal(1L, await cmd.ExecuteScalarAsync());
    }
}
