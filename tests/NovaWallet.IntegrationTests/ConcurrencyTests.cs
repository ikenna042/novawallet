using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using NovaWallet.Application;
using NovaWallet.IntegrationTests.Infrastructure;
using Npgsql;
using Xunit.Abstractions;

namespace NovaWallet.IntegrationTests;

/// <summary>
/// The hard constraints under load: many requests are released at the same instant against the same wallet,
/// through the full HTTP pipeline, against real PostgreSQL. Each test would fail if the row lock, lock
/// ordering or idempotency claim were removed.
/// </summary>
[Collection(LedgerCollection.Name)]
public sealed class ConcurrencyTests(LedgerApiFixture fixture, ITestOutputHelper output)
{
    private readonly LedgerClient _admin = fixture.Admin;

    private async Task<(LedgerClient Client, Guid WalletId)> NewFundedCustomerAsync(long balanceKobo)
    {
        var client = await LedgerClient.CustomerAsync(fixture.Factory);
        var wallet = await client.CreateWalletAsync();
        if (balanceKobo > 0)
            await (await _admin.CreditAsync(wallet.WalletId, balanceKobo)).EnsureStatusAsync(HttpStatusCode.Created);
        return (client, wallet.WalletId);
    }

    /// <summary>Starts all tasks together and waits for them, so requests genuinely interleave.</summary>
    private static async Task<T[]> Burst<T>(int count, Func<int, Task<T>> action)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, count)
            .Select(async i => { await start.Task; return await action(i); })
            .ToArray();
        start.SetResult();
        return await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task Parallel_transfers_from_one_wallet_never_overdraw_or_double_spend()
    {
        const int attempts = 200;
        const long amount = 100_00;          // ₦100
        const long opening = 1_000_00;       // ₦1,000 -> exactly 10 can succeed
        var (alice, aliceWallet) = await NewFundedCustomerAsync(opening);
        var (bob, bobWallet) = await NewFundedCustomerAsync(0);

        var sw = Stopwatch.StartNew();
        var responses = await Burst(attempts, _ =>
            alice.TransferAsync(aliceWallet, bobWallet, amount, Guid.NewGuid().ToString()));
        sw.Stop();

        var statuses = responses.GroupBy(r => r.StatusCode).ToDictionary(g => g.Key, g => g.Count());
        output.WriteLine($"{attempts} concurrent transfers in {sw.ElapsedMilliseconds} ms: " +
                         string.Join(", ", statuses.Select(s => $"{(int)s.Key}={s.Value}")));

        Assert.Equal(10, statuses.GetValueOrDefault(HttpStatusCode.Created));
        Assert.Equal(attempts - 10, statuses.GetValueOrDefault(HttpStatusCode.UnprocessableEntity));
        foreach (var rejected in responses.Where(r => r.StatusCode == HttpStatusCode.UnprocessableEntity))
            Assert.Equal("insufficient_funds", (await rejected.ReadProblemAsync()).Code);

        Assert.Equal(0, await alice.GetBalanceAsync(aliceWallet));
        Assert.Equal(opening, await bob.GetBalanceAsync(bobWallet));

        // The ledger agrees with the balance, and no entry ever recorded a negative running balance.
        await using var db = await fixture.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT COALESCE(SUM(CASE direction WHEN 'Credit' THEN amount_kobo ELSE -amount_kobo END), 0),
                   COALESCE(MIN(balance_after_kobo), 0),
                   COUNT(*) FILTER (WHERE direction = 'Debit')
            FROM ledger_entries WHERE wallet_id = @id
            """, db);
        cmd.Parameters.AddWithValue("id", aliceWallet);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        Assert.Equal(0, reader.GetInt64(0));
        Assert.True(reader.GetInt64(1) >= 0);
        Assert.Equal(10, reader.GetInt64(2));
    }

    [Fact]
    public async Task Opposing_transfers_between_two_wallets_do_not_deadlock_and_conserve_money()
    {
        const int perDirection = 100;
        const long opening = 10_000_00;
        var (alice, aliceWallet) = await NewFundedCustomerAsync(opening);
        var (bob, bobWallet) = await NewFundedCustomerAsync(opening);

        var sw = Stopwatch.StartNew();
        var responses = await Burst(perDirection * 2, i => i % 2 == 0
            ? alice.TransferAsync(aliceWallet, bobWallet, 1_00, Guid.NewGuid().ToString())
            : bob.TransferAsync(bobWallet, aliceWallet, 1_00, Guid.NewGuid().ToString()));
        sw.Stop();
        output.WriteLine($"{perDirection * 2} opposing transfers in {sw.ElapsedMilliseconds} ms");

        foreach (var response in responses)
            await response.EnsureStatusAsync(HttpStatusCode.Created);

        // Equal numbers each way, so both end where they started, and the total is conserved.
        Assert.Equal(opening, await alice.GetBalanceAsync(aliceWallet));
        Assert.Equal(opening, await bob.GetBalanceAsync(bobWallet));
    }

    [Fact]
    public async Task Concurrent_retries_with_the_same_idempotency_key_move_money_exactly_once()
    {
        var (alice, aliceWallet) = await NewFundedCustomerAsync(1_000_00);
        var (bob, bobWallet) = await NewFundedCustomerAsync(0);
        var key = Guid.NewGuid().ToString();

        var responses = await Burst(50, _ => alice.TransferAsync(aliceWallet, bobWallet, 250_00, key));

        foreach (var response in responses)
            await response.EnsureStatusAsync(HttpStatusCode.Created);

        var receipts = await Task.WhenAll(responses.Select(r => r.Content.ReadFromJsonAsync<TransactionReceipt>()));
        Assert.Single(receipts.Select(r => r!.TransactionId).Distinct());
        Assert.Single(responses, r => r.Headers.GetValues("Idempotent-Replayed").Single() == "false");

        Assert.Equal(750_00, await alice.GetBalanceAsync(aliceWallet));
        Assert.Equal(250_00, await bob.GetBalanceAsync(bobWallet));
    }

    [Fact]
    public async Task Concurrent_transfers_cannot_exceed_the_daily_limit()
    {
        // ₦1,000,000 available but the daily limit is ₦500,000: 25 x ₦20,000 fit, the rest must be refused.
        var (alice, aliceWallet) = await NewFundedCustomerAsync(1_000_000_00);
        var (_, bobWallet) = await NewFundedCustomerAsync(0);

        var responses = await Burst(40, _ =>
            alice.TransferAsync(aliceWallet, bobWallet, 20_000_00, Guid.NewGuid().ToString()));

        Assert.Equal(25, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        var rejected = responses.Where(r => r.StatusCode == HttpStatusCode.UnprocessableEntity).ToList();
        Assert.Equal(15, rejected.Count);
        foreach (var response in rejected)
            Assert.Equal("daily_limit_exceeded", (await response.ReadProblemAsync()).Code);

        Assert.Equal(500_000_00, await alice.GetBalanceAsync(aliceWallet));
    }

    [Fact]
    public async Task Many_senders_into_one_wallet_all_land()
    {
        var (receiver, receiverWallet) = await NewFundedCustomerAsync(0);
        var senders = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => NewFundedCustomerAsync(5_00)));

        var responses = await Burst(senders.Length, i =>
            senders[i].Client.TransferAsync(senders[i].WalletId, receiverWallet, 5_00, Guid.NewGuid().ToString()));

        foreach (var response in responses)
            await response.EnsureStatusAsync(HttpStatusCode.Created);
        Assert.Equal(senders.Length * 5_00, await receiver.GetBalanceAsync(receiverWallet));
    }
}
