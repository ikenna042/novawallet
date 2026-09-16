using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NovaWallet.Application.Abstractions;

namespace NovaWallet.Infrastructure.Persistence;

internal sealed class DbTransactionScope(IDbContextTransaction transaction) : ILedgerTransactionScope
{
    /// <summary>
    /// READ COMMITTED is enough because every decision that matters is made while holding a row lock
    /// (SELECT ... FOR UPDATE); SERIALIZABLE would add retry handling without adding safety here.
    /// </summary>
    public static async Task<ILedgerTransactionScope> BeginAsync(LedgerDbContext db, CancellationToken ct) =>
        new DbTransactionScope(await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct));

    public Task CommitAsync(CancellationToken ct) => transaction.CommitAsync(ct);

    // Disposing without a commit rolls back, which also releases every row lock taken in the transaction.
    public ValueTask DisposeAsync() => transaction.DisposeAsync();
}
