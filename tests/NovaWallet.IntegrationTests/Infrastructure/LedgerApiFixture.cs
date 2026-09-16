using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

namespace NovaWallet.IntegrationTests.Infrastructure;

/// <summary>
/// Starts one PostgreSQL database for the whole test run. Uses Testcontainers when Docker is available
/// (CI); otherwise set NOVAWALLET_TEST_DB to a connection string for an existing server (e.g. Postgres.app)
/// and a throwaway database is created and dropped around the run.
/// </summary>
public sealed class LedgerApiFixture : IAsyncLifetime
{
    public const string ExternalDbVariable = "NOVAWALLET_TEST_DB";
    public const string SigningKey = "integration-test-signing-key-0123456789abcdef";
    public const string AdminEmail = "admin@novawallet.test";
    public const string AdminPassword = "Integration-Admin-2026";

    private PostgreSqlContainer? _container;
    private string? _externalAdminConnectionString;
    private string? _databaseName;

    public string ConnectionString { get; private set; } = null!;
    public LedgerApiFactory Factory { get; private set; } = null!;

    /// <summary>Signed in once as the seeded administrator and shared by all tests.</summary>
    public LedgerClient Admin { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable(ExternalDbVariable);
        string baseConnectionString;
        if (!string.IsNullOrWhiteSpace(external))
        {
            _externalAdminConnectionString = external;
            _databaseName = $"novawallet_it_{Guid.NewGuid():N}";
            await using (var admin = new NpgsqlConnection(external))
            {
                await admin.OpenAsync();
                await using var create = new NpgsqlCommand($"CREATE DATABASE \"{_databaseName}\"", admin);
                await create.ExecuteNonQueryAsync();
            }
            baseConnectionString = new NpgsqlConnectionStringBuilder(external) { Database = _databaseName }.ConnectionString;
        }
        else
        {
            _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
            await _container.StartAsync();
            baseConnectionString = _container.GetConnectionString();
        }

        // Keep well under PostgreSQL's default max_connections (100) even with 200 concurrent requests;
        // extra requests wait for a pooled connection, which is exactly what production would do.
        ConnectionString = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            MaxPoolSize = 40,
            Timeout = 30,
        }.ConnectionString;

        Factory = new LedgerApiFactory(ConnectionString);
        _ = Factory.Server; // boots the host, applies migrations and seeds the admin
        Admin = await LedgerClient.AdminAsync(Factory);
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        NpgsqlConnection.ClearAllPools();

        if (_container is not null)
            await _container.DisposeAsync();

        if (_externalAdminConnectionString is not null)
        {
            await using var admin = new NpgsqlConnection(_externalAdminConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    public async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }
}

public sealed class LedgerApiFactory(string connectionString, IReadOnlyDictionary<string, string?>? overrides = null)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Ledger"] = connectionString,
            ["Jwt:SigningKey"] = LedgerApiFixture.SigningKey,
            ["Auth:SeedAdmin:Email"] = LedgerApiFixture.AdminEmail,
            ["Auth:SeedAdmin:Password"] = LedgerApiFixture.AdminPassword,
            ["Auth:AccessTokenMinutes"] = "60",
            ["Database:MigrateOnStartup"] = "true",
            ["RateLimiting:Transfers:PermitLimit"] = "100000",
            ["RateLimiting:Auth:PermitLimit"] = "100000",
            ["Outbox:PollInterval"] = "00:00:00.200",
            ["Logging:UseJson"] = "false",
            ["Serilog:MinimumLevel:Default"] = "Warning",
        };
        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
            settings[key] = value;

        foreach (var (key, value) in settings)
            builder.UseSetting(key, value);
    }
}

[CollectionDefinition(Name)]
public sealed class LedgerCollection : ICollectionFixture<LedgerApiFixture>
{
    public const string Name = "ledger";
}
