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

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<DailyLimitPolicy>();
        services.AddSingleton<RequestGuard>();
        services.AddScoped<WalletService>();
        services.AddScoped<TransferService>();
        return services;
    }
}
