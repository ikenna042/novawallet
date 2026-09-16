using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using NovaWallet.Application;

namespace NovaWallet.IntegrationTests.Infrastructure;

/// <summary>Thin typed wrapper over the HTTP API, signed in as one user through the real auth endpoints.</summary>
public sealed class LedgerClient
{
    public const string DefaultPassword = "Correct-Horse-Battery-42";

    private LedgerClient(HttpClient http, AuthTokens tokens)
    {
        Http = http;
        Tokens = tokens;
    }

    public HttpClient Http { get; }
    public AuthTokens Tokens { get; }
    public Guid UserId => Tokens.User.UserId;
    public string Email => Tokens.User.Email;

    /// <summary>The JWT subject, which is also the wallet's customer id.</summary>
    public string Subject => UserId.ToString("N");

    public static string NewEmail(string prefix = "user") => $"{prefix}-{Guid.NewGuid():N}@example.test";

    /// <summary>
    /// Registers a new customer and signs in. <paramref name="authFactory"/> lets a test that runs the API on a
    /// fake clock obtain tokens from a host on the real clock.
    /// </summary>
    public static async Task<LedgerClient> CustomerAsync(
        WebApplicationFactory<Program> factory, WebApplicationFactory<Program>? authFactory = null, string? email = null)
    {
        email ??= NewEmail();
        var anonymous = (authFactory ?? factory).CreateClient();
        await (await anonymous.PostAsJsonAsync("/api/v1/auth/register", new { email, password = DefaultPassword }))
            .EnsureStatusAsync(HttpStatusCode.Created);
        return await LoginAsync(factory, email, DefaultPassword, authFactory);
    }

    public static async Task<LedgerClient> AdminAsync(
        WebApplicationFactory<Program> factory, WebApplicationFactory<Program>? authFactory = null) =>
        await LoginAsync(factory, LedgerApiFixture.AdminEmail, LedgerApiFixture.AdminPassword, authFactory);

    public static async Task<LedgerClient> LoginAsync(
        WebApplicationFactory<Program> factory, string email, string password,
        WebApplicationFactory<Program>? authFactory = null)
    {
        var tokens = await LoginTokensAsync((authFactory ?? factory).CreateClient(), email, password);
        return For(factory, tokens);
    }

    public static async Task<AuthTokens> LoginTokensAsync(HttpClient anonymous, string email, string password)
    {
        var response = await anonymous.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        await response.EnsureStatusAsync(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<AuthTokens>())!;
    }

    public static LedgerClient For(WebApplicationFactory<Program> factory, AuthTokens tokens)
    {
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return new LedgerClient(http, tokens);
    }

    public async Task<WalletResponse> CreateWalletAsync()
    {
        var response = await Http.PostAsJsonAsync("/api/v1/wallets", new { });
        await response.EnsureStatusAsync(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<WalletResponse>())!;
    }

    public Task<HttpResponseMessage> CreditAsync(Guid walletId, long amountKobo, string? reference = null) =>
        Http.PostAsJsonAsync($"/api/v1/wallets/{walletId}/credit", new
        {
            amountKobo,
            reference = reference ?? $"NIP{Guid.NewGuid():N}",
            narration = "Inbound NIP transfer",
        });

    public Task<HttpResponseMessage> TransferAsync(Guid from, Guid to, long amountKobo, string? idempotencyKey = null, string? narration = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transfers")
        {
            Content = JsonContent.Create(new { sourceWalletId = from, destinationWalletId = to, amountKobo, narration }),
        };
        if (idempotencyKey is not null)
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        return Http.SendAsync(request);
    }

    public async Task<long> GetBalanceAsync(Guid walletId)
    {
        var response = await Http.GetAsync($"/api/v1/wallets/{walletId}/balance");
        await response.EnsureStatusAsync(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<BalanceResponse>())!.BalanceKobo;
    }

    public async Task<StatementPage> GetStatementAsync(Guid walletId, int? limit = null, string? cursor = null)
    {
        var url = $"/api/v1/wallets/{walletId}/statement?limit={limit ?? 100}";
        if (cursor is not null)
            url += $"&cursor={cursor}";
        var response = await Http.GetAsync(url);
        await response.EnsureStatusAsync(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<StatementPage>())!;
    }
}

public static class HttpAssertions
{
    public static async Task EnsureStatusAsync(this HttpResponseMessage response, HttpStatusCode expected)
    {
        if (response.StatusCode != expected)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.Fail($"Expected {(int)expected} but got {(int)response.StatusCode}: {body}");
        }
    }

    public static async Task<ProblemBody> ReadProblemAsync(this HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return (await response.Content.ReadFromJsonAsync<ProblemBody>())!;
    }
}

public sealed record ProblemBody(string? Type, string? Title, int? Status, string? Detail, string? Code, string? TraceId, string? CorrelationId);
