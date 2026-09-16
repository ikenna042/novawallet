using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NovaWallet.Application.Abstractions;
using NovaWallet.Infrastructure.Outbox;
using NovaWallet.Infrastructure.Persistence;

namespace NovaWallet.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Ledger";

    public static IServiceCollection AddLedgerInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Resolved lazily so configuration added late (e.g. by test hosts) is honoured.
        services.AddDbContext<LedgerDbContext>((sp, options) =>
        {
            var connectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString(ConnectionStringName);
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException($"Connection string 'ConnectionStrings:{ConnectionStringName}' is not configured.");
            ConfigureDbContext(options, connectionString);
        });
        services.AddScoped<ILedgerStore, LedgerStore>();

        services.Configure<OutboxOptions>(configuration.GetSection(OutboxOptions.SectionName));
        services.AddSingleton<IEventPublisher, LoggingEventPublisher>();
        services.AddHostedService<OutboxProcessor>();
        return services;
    }

    internal static void ConfigureDbContext(DbContextOptionsBuilder options, string connectionString) =>
        // No EnableRetryOnFailure: an execution strategy that transparently re-runs statements is incompatible
        // with the explicit transactions the transfer relies on.
        options
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
            .UseSnakeCaseNamingConvention();
}
