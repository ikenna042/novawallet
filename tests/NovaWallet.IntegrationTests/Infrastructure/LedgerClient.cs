using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NovaWallet.Api.Auth;
using NovaWallet.Application;

namespace NovaWallet.IntegrationTests.Infrastructure;

/// <summary>Thin typed wrapper over the HTTP API, authenticated as one subject.</summary>
public sealed class LedgerClient(HttpClient http, string subject)
{
    public string Subject { get; } = subject;
    public HttpClient Http { get; } = http;

    public static LedgerClient Create(WebApplicationFactory<Program> factory, string role = Roles.Customer, string? subject = null)
    {
        subject ??= $"{role}-{Guid.NewGuid():N}";
        // Always sign with the real clock, even when the host under test runs on a fake TimeProvider.
        var issuer = new DevTokenIssuer(factory.Services.GetRequiredService<IOptions<JwtOptions>>(), TimeProvider.System);
        var token = issuer.Issue(subject, role, TimeSpan.FromMinutes(30));
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return new LedgerClient(http, subject);
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
