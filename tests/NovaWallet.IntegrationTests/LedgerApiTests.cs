using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NovaWallet.Api.Auth;
using NovaWallet.Application;
using NovaWallet.IntegrationTests.Infrastructure;
using Npgsql;

namespace NovaWallet.IntegrationTests;

[Collection(LedgerCollection.Name)]
public sealed class LedgerApiTests(LedgerApiFixture fixture)
{
    private LedgerClient Admin => fixture.Admin;

    /// <param name="factory">Host to talk to (defaults to the shared one).</param>
    /// <param name="authFactory">Host to sign in through, when <paramref name="factory"/> runs on a fake clock.</param>
    private async Task<(LedgerClient Client, Guid WalletId)> NewCustomerAsync(
        long balanceKobo = 0, WebApplicationFactory<Program>? factory = null, WebApplicationFactory<Program>? authFactory = null)
    {
        factory ??= fixture.Factory;
        var client = await LedgerClient.CustomerAsync(factory, authFactory);
        var wallet = await client.CreateWalletAsync();
        if (balanceKobo > 0)
        {
            var admin = ReferenceEquals(factory, fixture.Factory) ? Admin : await LedgerClient.AdminAsync(factory, authFactory);
            await (await admin.CreditAsync(wallet.WalletId, balanceKobo)).EnsureStatusAsync(HttpStatusCode.Created);
        }
        return (client, wallet.WalletId);
    }

    // ---------- wallets & balances ----------

    [Fact]
    public async Task New_wallet_has_zero_ngn_balance()
    {
        var client = await LedgerClient.CustomerAsync(fixture.Factory);
        var response = await client.Http.PostAsJsonAsync("/api/v1/wallets", new { });

        await response.EnsureStatusAsync(HttpStatusCode.Created);
        Assert.NotNull(response.Headers.Location);
        var wallet = (await response.Content.ReadFromJsonAsync<WalletResponse>())!;
        Assert.Equal(client.Subject, wallet.CustomerId);

        var balance = (await client.Http.GetFromJsonAsync<BalanceResponse>($"/api/v1/wallets/{wallet.WalletId}/balance"))!;
        Assert.Equal(0, balance.BalanceKobo);
        Assert.Equal("NGN", balance.Currency);
        Assert.Equal("₦0.00", balance.BalanceDisplay);
    }

    [Fact]
    public async Task Second_wallet_for_same_customer_is_a_conflict()
    {
        var (client, _) = await NewCustomerAsync();
        var response = await client.Http.PostAsJsonAsync("/api/v1/wallets", new { });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("wallet_already_exists", (await response.ReadProblemAsync()).Code);
    }

    [Fact]
    public async Task Customer_cannot_create_a_wallet_for_someone_else_but_admin_can()
    {
        var customer = await LedgerClient.CustomerAsync(fixture.Factory);
        var other = await LedgerClient.CustomerAsync(fixture.Factory);

        var denied = await customer.Http.PostAsJsonAsync("/api/v1/wallets", new { customerId = other.Subject });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var created = await Admin.Http.PostAsJsonAsync("/api/v1/wallets", new { customerId = other.Subject });
        await created.EnsureStatusAsync(HttpStatusCode.Created);
        Assert.Equal(other.Subject, (await created.Content.ReadFromJsonAsync<WalletResponse>())!.CustomerId);

        // Wallets can only belong to registered users.
        var unknown = await Admin.Http.PostAsJsonAsync("/api/v1/wallets", new { customerId = Guid.NewGuid().ToString("N") });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task Customer_cannot_see_another_customers_wallet()
    {
        var (_, aliceWallet) = await NewCustomerAsync(1_000_00);
        var mallory = await LedgerClient.CustomerAsync(fixture.Factory);

        foreach (var path in new[] { "balance", "statement" })
        {
            var response = await mallory.Http.GetAsync($"/api/v1/wallets/{aliceWallet}/{path}");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    // ---------- credits ----------

    [Fact]
    public async Task Customers_cannot_credit_wallets()
    {
        var (client, wallet) = await NewCustomerAsync();
        var response = await client.CreditAsync(wallet, 1_000_00);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await client.GetBalanceAsync(wallet));
    }

    [Fact]
    public async Task Credit_is_idempotent_on_the_nip_reference()
    {
        var (client, wallet) = await NewCustomerAsync();
        var reference = $"NIP{Guid.NewGuid():N}";

        var first = await Admin.CreditAsync(wallet, 5_000_00, reference);
        var second = await Admin.CreditAsync(wallet, 5_000_00, reference);
        await first.EnsureStatusAsync(HttpStatusCode.Created);
        await second.EnsureStatusAsync(HttpStatusCode.Created);
        Assert.Equal("true", second.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(
            (await first.Content.ReadFromJsonAsync<TransactionReceipt>())!.TransactionId,
            (await second.Content.ReadFromJsonAsync<TransactionReceipt>())!.TransactionId);
        Assert.Equal(5_000_00, await client.GetBalanceAsync(wallet));

        var different = await Admin.CreditAsync(wallet, 1_00, reference);
        Assert.Equal(HttpStatusCode.Conflict, different.StatusCode);
        Assert.Equal("duplicate_reference", (await different.ReadProblemAsync()).Code);
    }

    // ---------- transfers ----------

    [Fact]
    public async Task Transfer_moves_money_and_returns_only_the_senders_balance()
    {
        var (alice, aliceWallet) = await NewCustomerAsync(10_000_00);
        var (bob, bobWallet) = await NewCustomerAsync();

        var response = await alice.TransferAsync(bobWallet, 2_500_50, Guid.NewGuid().ToString(), "Rent");
        await response.EnsureStatusAsync(HttpStatusCode.Created);
        Assert.Equal("false", response.Headers.GetValues("Idempotent-Replayed").Single());

        var receipt = (await response.Content.ReadFromJsonAsync<TransactionReceipt>())!;
        Assert.Equal(7_499_50, receipt.BalanceAfterKobo);
        Assert.Equal("Transfer", receipt.Type);
        Assert.Equal(7_499_50, await alice.GetBalanceAsync(aliceWallet));
        Assert.Equal(2_500_50, await bob.GetBalanceAsync(bobWallet));
    }

    [Fact]
    public async Task Replayed_transfer_returns_the_same_receipt_and_moves_nothing()
    {
        var (alice, aliceWallet) = await NewCustomerAsync(10_000_00);
        var (_, bobWallet) = await NewCustomerAsync();
        var key = Guid.NewGuid().ToString();

        var first = await alice.TransferAsync(bobWallet, 1_000_00, key);
        var replay = await alice.TransferAsync(bobWallet, 1_000_00, key);

        await replay.EnsureStatusAsync(HttpStatusCode.Created);
        Assert.Equal("true", replay.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(await first.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        Assert.Equal(9_000_00, await alice.GetBalanceAsync(aliceWallet));
    }

    [Fact]
    public async Task Reusing_a_key_with_a_different_payload_is_rejected()
    {
        var (alice, aliceWallet) = await NewCustomerAsync(10_000_00);
        var (_, bobWallet) = await NewCustomerAsync();
        var key = Guid.NewGuid().ToString();

        await (await alice.TransferAsync(bobWallet, 1_000_00, key)).EnsureStatusAsync(HttpStatusCode.Created);
        var reused = await alice.TransferAsync(bobWallet, 9_000_00, key);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.StatusCode);
        Assert.Equal("idempotency_key_reused", (await reused.ReadProblemAsync()).Code);
        Assert.Equal(9_000_00, await alice.GetBalanceAsync(aliceWallet));
    }

    [Fact]
    public async Task Rejected_transfer_replays_the_same_error_even_after_a_top_up()
    {
        var (alice, aliceWallet) = await NewCustomerAsync(1_00);
        var (_, bobWallet) = await NewCustomerAsync();
        var key = Guid.NewGuid().ToString();

        var first = await alice.TransferAsync(bobWallet, 5_00, key);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, first.StatusCode);

        await (await Admin.CreditAsync(aliceWallet, 100_00)).EnsureStatusAsync(HttpStatusCode.Created);
        var retry = await alice.TransferAsync(bobWallet, 5_00, key);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, retry.StatusCode);
        Assert.Equal("insufficient_funds", (await retry.ReadProblemAsync()).Code);
        Assert.Equal(101_00, await alice.GetBalanceAsync(aliceWallet));
    }

    [Fact]
    public async Task Idempotency_keys_are_scoped_per_customer()
    {
        var (alice, aliceWallet) = await NewCustomerAsync(1_000_00);
        var (bob, bobWallet) = await NewCustomerAsync(1_000_00);
        var sharedKey = Guid.NewGuid().ToString();

        await (await alice.TransferAsync(bobWallet, 1_00, sharedKey)).EnsureStatusAsync(HttpStatusCode.Created);
        var bobs = await bob.TransferAsync(aliceWallet, 1_00, sharedKey);

        await bobs.EnsureStatusAsync(HttpStatusCode.Created);
        Assert.Equal("false", bobs.Headers.GetValues("Idempotent-Replayed").Single());
    }

    [Fact]
    public async Task Transfer_without_idempotency_key_is_rejected()
    {
        var (alice, aliceWallet) = await NewCustomerAsync(1_000_00);
        var (_, bobWallet) = await NewCustomerAsync();

        var response = await alice.TransferAsync(bobWallet, 1_00, idempotencyKey: null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_error", (await response.ReadProblemAsync()).Code);
    }

    [Fact]
    public async Task Naming_a_source_wallet_is_rejected()
    {
        // The source always comes from the signed-in user; a request can't even name one.
        var (alice, aliceWallet) = await NewCustomerAsync(1_000_00);
        var (mallory, malloryWallet) = await NewCustomerAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transfers")
        {
            Content = JsonContent.Create(new { sourceWalletId = aliceWallet, destinationWalletId = malloryWallet, amountKobo = 1_000_00 }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await mallory.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1_000_00, await alice.GetBalanceAsync(aliceWallet));
        Assert.Equal(0, await mallory.GetBalanceAsync(malloryWallet));
    }

    [Fact]
    public async Task Transfer_debits_the_signed_in_users_wallet()
    {
        var (alice, aliceWallet) = await NewCustomerAsync(1_000_00);
        var (bob, bobWallet) = await NewCustomerAsync(1_000_00);

        var response = await bob.TransferAsync(aliceWallet, 300_00, Guid.NewGuid().ToString());
        await response.EnsureStatusAsync(HttpStatusCode.Created);
        var receipt = (await response.Content.ReadFromJsonAsync<TransactionReceipt>())!;

        Assert.Equal(bobWallet, receipt.SourceWalletId);
        Assert.Equal(700_00, await bob.GetBalanceAsync(bobWallet));
        Assert.Equal(1_300_00, await alice.GetBalanceAsync(aliceWallet));
    }

    [Fact]
    public async Task Customer_without_a_wallet_cannot_transfer()
    {
        var (_, someWallet) = await NewCustomerAsync();
        var walletless = await LedgerClient.CustomerAsync(fixture.Factory);

        var response = await walletless.TransferAsync(someWallet, 1_00, Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await response.ReadProblemAsync();
        Assert.Equal("wallet_not_found", problem.Code);
        Assert.Contains("don't have a wallet", problem.Detail);
    }

    [Theory]
    [InlineData("""{"destinationWalletId":"{dst}","amountKobo":100.5}""")]
    [InlineData("""{"destinationWalletId":"{dst}","amountKobo":"100"}""")]
    [InlineData("""{"destinationWalletId":"{dst}","amountKobo":-100}""")]
    [InlineData("""{"destinationWalletId":"{dst}","amountKobo":0}""")]
    [InlineData("""{"destinationWalletId":"{dst}","amount":100}""")]
    [InlineData("""{"destinationWalletId":"{src}","amountKobo":100}""")]
    [InlineData("""{"destinationWalletId":"{dst}","amountKobo":9223372036854775807}""")]
    public async Task Malformed_transfer_requests_are_rejected_with_problem_details(string body)
    {
        var (alice, aliceWallet) = await NewCustomerAsync(1_000_00);
        var (_, bobWallet) = await NewCustomerAsync();
        var json = body.Replace("{src}", aliceWallet.ToString()).Replace("{dst}", bobWallet.ToString());

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transfers")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await alice.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(1_000_00, await alice.GetBalanceAsync(aliceWallet));
    }

    [Fact]
    public async Task Daily_limit_resets_at_midnight_west_africa_time()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 16, 21, 0, 0, TimeSpan.Zero)); // 22:00 WAT
        await using var timed = fixture.Factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(clock)));

        // Tokens come from the real-clock host; the ledger under test runs on the fake clock.
        var (alice, aliceWallet) = await NewCustomerAsync(1_000_000_00, timed, authFactory: fixture.Factory);
        var (_, bobWallet) = await NewCustomerAsync(0, timed, authFactory: fixture.Factory);

        await (await alice.TransferAsync(bobWallet, 500_000_00, Guid.NewGuid().ToString()))
            .EnsureStatusAsync(HttpStatusCode.Created);

        clock.SetUtcNow(new DateTimeOffset(2026, 9, 16, 22, 59, 59, TimeSpan.Zero)); // 23:59:59 WAT
        var sameDay = await alice.TransferAsync(bobWallet, 1, Guid.NewGuid().ToString());
        Assert.Equal("daily_limit_exceeded", (await sameDay.ReadProblemAsync()).Code);

        clock.SetUtcNow(new DateTimeOffset(2026, 9, 16, 23, 0, 0, TimeSpan.Zero)); // 00:00 WAT next day
        await (await alice.TransferAsync(bobWallet, 500_000_00, Guid.NewGuid().ToString()))
            .EnsureStatusAsync(HttpStatusCode.Created);
    }

    // ---------- statement ----------

    [Fact]
    public async Task Statement_is_paginated_newest_first_without_gaps_or_duplicates()
    {
        var (client, wallet) = await NewCustomerAsync();
        for (var i = 1; i <= 25; i++)
            await (await Admin.CreditAsync(wallet, i)).EnsureStatusAsync(HttpStatusCode.Created);

        var amounts = new List<long>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await client.GetStatementAsync(wallet, limit: 10, cursor);
            amounts.AddRange(page.Items.Select(x => x.AmountKobo));
            cursor = page.NextCursor;
            pages++;
        } while (cursor is not null);

        Assert.Equal(3, pages);
        Assert.Equal(Enumerable.Range(1, 25).Reverse().Select(i => (long)i), amounts);
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=101")]
    [InlineData("cursor=abc")]
    [InlineData("cursor=-5")]
    public async Task Statement_rejects_bad_paging_parameters(string query)
    {
        var (client, wallet) = await NewCustomerAsync();
        var response = await client.Http.GetAsync($"/api/v1/wallets/{wallet}/statement?{query}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------- audit trail ----------

    [Fact]
    public async Task Audit_trail_records_every_mutation_with_an_intact_hash_chain()
    {
        var (alice, aliceWallet) = await NewCustomerAsync(1_000_00);
        var (_, bobWallet) = await NewCustomerAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transfers")
        {
            Content = JsonContent.Create(new { destinationWalletId = bobWallet, amountKobo = 400_00 }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.Add("X-Correlation-ID", "test-correlation-0001");
        var response = await alice.Http.SendAsync(request);
        await response.EnsureStatusAsync(HttpStatusCode.Created);
        Assert.Equal("test-correlation-0001", response.Headers.GetValues("X-Correlation-ID").Single());

        var trail = (await Admin.Http.GetFromJsonAsync<AuditTrailResponse>($"/api/v1/wallets/{aliceWallet}/audit"))!;
        Assert.True(trail.ChainIntact);
        Assert.Collection(trail.Records,
            credit => Assert.Equal(("CREDIT", 0L, 1_000_00L), (credit.Action, credit.BalanceBeforeKobo, credit.BalanceAfterKobo)),
            debit =>
            {
                Assert.Equal(("TRANSFER_DEBIT", -400_00L, 600_00L), (debit.Action, debit.DeltaKobo, debit.BalanceAfterKobo));
                Assert.Equal("test-correlation-0001", debit.CorrelationId);
                Assert.Equal(alice.Subject, debit.Actor);
            });

        var forbidden = await alice.Http.GetAsync($"/api/v1/wallets/{aliceWallet}/audit");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Theory]
    [InlineData("UPDATE audit_log SET delta_kobo = delta_kobo + 1")]
    [InlineData("DELETE FROM audit_log")]
    [InlineData("TRUNCATE audit_log")]
    [InlineData("UPDATE ledger_entries SET amount_kobo = 1")]
    [InlineData("DELETE FROM ledger_transactions")]
    public async Task Ledger_and_audit_tables_are_append_only_in_the_database(string sql)
    {
        await NewCustomerAsync(1_00); // make sure rows exist
        await using var db = await fixture.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, db);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Contains("append-only", ex.MessageText);
    }

    [Fact]
    public async Task Database_refuses_a_negative_balance_even_if_the_application_tried()
    {
        var (_, wallet) = await NewCustomerAsync(1_00);
        await using var db = await fixture.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("UPDATE wallets SET balance_kobo = -1 WHERE id = @id", db);
        cmd.Parameters.AddWithValue("id", wallet);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    // ---------- outbox ----------

    [Fact]
    public async Task Transfer_completed_event_is_published_from_the_outbox()
    {
        var (alice, aliceWallet) = await NewCustomerAsync(1_000_00);
        var (_, bobWallet) = await NewCustomerAsync();
        var response = await alice.TransferAsync(bobWallet, 1_00, Guid.NewGuid().ToString());
        var receipt = (await response.Content.ReadFromJsonAsync<TransactionReceipt>())!;

        await using var db = await fixture.OpenConnectionAsync();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT processed_at IS NOT NULL FROM outbox_messages WHERE payload->>'transactionId' = @id", db);
            cmd.Parameters.AddWithValue("id", receipt.TransactionId.ToString());
            if (await cmd.ExecuteScalarAsync() is true)
                break;
            Assert.True(DateTime.UtcNow < deadline, "Outbox message was not processed in time.");
            await Task.Delay(200);
        }
    }

    // ---------- auth, rate limiting, operability ----------

    [Fact]
    public async Task Requests_without_a_valid_token_are_rejected_with_problem_details()
    {
        var anonymous = fixture.Factory.CreateClient();
        var noToken = await anonymous.GetAsync($"/api/v1/wallets/{Guid.NewGuid()}/balance");
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);
        Assert.Equal("application/problem+json", noToken.Content.Headers.ContentType?.MediaType);

        // A forger who knows the admin's user id but not the signing key.
        var forger = Issuer(signingKey: "a-completely-different-signing-key-9876543210", TimeProvider.System);
        foreach (var token in new[]
                 {
                     forger.Issue(Admin.Subject, Roles.Admin, 1, TimeSpan.FromMinutes(5)).Token, // wrong key
                     "not-a-jwt",
                     // alg=none token claiming admin
                     "eyJhbGciOiJub25lIiwidHlwIjoiSldUIn0.eyJzdWIiOiJ4Iiwicm9sZSI6ImFkbWluIiwidmVyIjoiMSJ9.",
                 })
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/wallets/{Guid.NewGuid()}/balance");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(request)).StatusCode);
        }
    }

    [Fact]
    public async Task Expired_tokens_are_rejected()
    {
        var customer = await LedgerClient.CustomerAsync(fixture.Factory);
        // Correct key, real user, current version, but issued two hours ago with a five-minute lifetime.
        var issuer = Issuer(LedgerApiFixture.SigningKey, new FakeTimeProvider(DateTimeOffset.UtcNow.AddHours(-2)));
        var http = fixture.Factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            issuer.Issue(customer.Subject, Roles.Customer, 1, TimeSpan.FromMinutes(5)).Token);

        var response = await http.PostAsJsonAsync("/api/v1/wallets", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Transfer_endpoint_is_rate_limited_per_customer()
    {
        await using var limited = new LedgerApiFactory(fixture.ConnectionString, new Dictionary<string, string?>
        {
            ["RateLimiting:Transfers:PermitLimit"] = "3",
            ["RateLimiting:Transfers:WindowSeconds"] = "60",
        });
        var (alice, aliceWallet) = await NewCustomerAsync(1_000_00, limited);
        var (bob, bobWallet) = await NewCustomerAsync(1_000_00, limited);

        for (var i = 0; i < 3; i++)
            await (await alice.TransferAsync(bobWallet, 1_00, Guid.NewGuid().ToString()))
                .EnsureStatusAsync(HttpStatusCode.Created);

        var throttled = await alice.TransferAsync(bobWallet, 1_00, Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.Equal("rate_limited", (await throttled.ReadProblemAsync()).Code);
        Assert.True(throttled.Headers.Contains("Retry-After"));

        // Another customer has their own budget.
        await (await bob.TransferAsync(aliceWallet, 1_00, Guid.NewGuid().ToString()))
            .EnsureStatusAsync(HttpStatusCode.Created);
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/swagger/v1/swagger.json")]
    public async Task Health_and_openapi_endpoints_are_public(string path)
    {
        var response = await fixture.Factory.CreateClient().GetAsync(path);
        await response.EnsureStatusAsync(HttpStatusCode.OK);
    }

    private static JwtTokenIssuer Issuer(string signingKey, TimeProvider clock) => new(
        Microsoft.Extensions.Options.Options.Create(new JwtOptions { SigningKey = signingKey }),
        Microsoft.Extensions.Options.Options.Create(new AuthOptions()),
        clock);
}
