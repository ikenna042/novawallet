using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NovaWallet.Infrastructure.Persistence;

/// <summary>Used only by <c>dotnet ef</c> to build migrations; never at runtime.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<LedgerDbContext>
{
    public LedgerDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<LedgerDbContext>();
        DependencyInjection.ConfigureDbContext(options, "Host=localhost;Database=novawallet_design;Username=design");
        return new LedgerDbContext(options.Options);
    }
}
