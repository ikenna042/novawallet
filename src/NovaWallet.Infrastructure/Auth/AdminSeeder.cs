using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NovaWallet.Application;
using NovaWallet.Application.Abstractions;
using NovaWallet.Domain;

namespace NovaWallet.Infrastructure.Auth;

/// <summary>
/// Creates the first administrator from configuration (Auth:SeedAdmin) if that account doesn't exist yet.
/// Public registration only ever creates customers, so this is the only way in for a fresh installation.
/// Safe to run on every start: an existing account is left untouched (its password is never overwritten).
/// </summary>
public static class AdminSeeder
{
    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var options = sp.GetRequiredService<IOptions<AuthOptions>>().Value.SeedAdmin;
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AdminSeeder));

        if (string.IsNullOrWhiteSpace(options.Email) || string.IsNullOrEmpty(options.Password))
        {
            logger.LogInformation("No seed administrator configured (Auth:SeedAdmin)");
            return;
        }

        var email = Credentials.Email(options.Email);
        var password = Credentials.Password(options.Password);
        if (password == SeedAdminOptions.DemoPassword)
            logger.LogWarning("The seed administrator uses the published demo password. Set ADMIN_PASSWORD for anything but a local demo.");

        var users = sp.GetRequiredService<IUserStore>();
        var hasher = sp.GetRequiredService<IPasswordHasher>();
        var now = sp.GetRequiredService<TimeProvider>().GetLedgerNow();

        var admin = new User(Guid.NewGuid(), email, Credentials.FullName(options.FullName), hasher.Hash(password),
            UserRole.Admin, now);

        await using var tx = await users.BeginAsync(ct);
        if (!await users.TryAddAsync(admin, ct))
        {
            logger.LogInformation("Seed administrator already exists");
            return;
        }

        users.Add(new AdminAction("system", AdminActionTypes.CreateAdmin, "user", admin.SubjectId, null, null, now));
        await users.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        logger.LogInformation("Seed administrator {UserId} created", admin.Id);
    }
}
