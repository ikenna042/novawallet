using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NovaWallet.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddLedgerApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<LedgerOptions>()
            .Bind(configuration.GetSection(LedgerOptions.SectionName))
            .Validate(o => o.DailyTransferLimitKobo > 0 && o.MaxTransactionAmountKobo > 0,
                "Ledger limits must be positive.")
            .ValidateOnStart();

        services.AddOptions<AuthOptions>()
            .Bind(configuration.GetSection(AuthOptions.SectionName))
            .Validate(o => o.AccessTokenMinutes is > 0 and <= 60, "Auth:AccessTokenMinutes must be 1-60.")
            .Validate(o => o.RefreshTokenDays is > 0 and <= 90, "Auth:RefreshTokenDays must be 1-90.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<DailyLimitPolicy>();
        services.AddSingleton<RequestGuard>();
        services.AddScoped<WalletService>();
        services.AddScoped<TransferService>();
        services.AddScoped<AuthService>();
        services.AddScoped<AdminService>();
        return services;
    }
}
