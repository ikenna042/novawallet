using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NovaWallet.Domain;

/// <summary>
/// Append-only record of a single balance mutation. The table is protected by database triggers that reject
/// UPDATE/DELETE/TRUNCATE, and each wallet's records form a SHA-256 hash chain so that any out-of-band edit
/// (e.g. by someone who disabled the trigger) is detectable by <see cref="AuditChain.Verify"/>.
/// The chain is per wallet because the wallet row lock is what serialises writers; a single global chain
/// would need a global lock.
/// </summary>
public sealed class AuditRecord
{
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    private AuditRecord() { } // EF Core

    public AuditRecord(
        Guid walletId,
        Guid transactionId,
        string action,
        long deltaKobo,
        long balanceBeforeKobo,
        long balanceAfterKobo,
        string actor,
        string? correlationId,
        DateTimeOffset occurredAt,
        string previousHash)
    {
        if (checked(balanceBeforeKobo + deltaKobo) != balanceAfterKobo)
            throw new InvalidOperationException("Audit record does not balance: before + delta != after.");

        WalletId = walletId;
        TransactionId = transactionId;
        Action = action;
        DeltaKobo = deltaKobo;
        BalanceBeforeKobo = balanceBeforeKobo;
        BalanceAfterKobo = balanceAfterKobo;
        Actor = actor;
        CorrelationId = correlationId;
        OccurredAt = occurredAt;
        PreviousHash = previousHash;
        Hash = ComputeHash();
    }

    public long Id { get; private set; }
    public Guid WalletId { get; private set; }
    public Guid TransactionId { get; private set; }
    public string Action { get; private set; } = null!;

    /// <summary>Signed change in kobo: negative for debits.</summary>
    public long DeltaKobo { get; private set; }

    public long BalanceBeforeKobo { get; private set; }
    public long BalanceAfterKobo { get; private set; }
    public string Actor { get; private set; } = null!;
    public string? CorrelationId { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public string PreviousHash { get; private set; } = null!;
    public string Hash { get; private set; } = null!;

    public string ComputeHash()
    {
        // OccurredAt must already be truncated to microseconds (PostgreSQL's precision), otherwise the
        // value read back from the database would hash differently from the value written.
        var canonical = string.Join('|',
            PreviousHash,
            WalletId.ToString("N"),
            TransactionId.ToString("N"),
            Action,
            DeltaKobo.ToString(CultureInfo.InvariantCulture),
            BalanceBeforeKobo.ToString(CultureInfo.InvariantCulture),
            BalanceAfterKobo.ToString(CultureInfo.InvariantCulture),
            Actor,
            CorrelationId ?? string.Empty,
            OccurredAt.UtcTicks.ToString(CultureInfo.InvariantCulture));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

public static class AuditActions
{
    public const string Credit = "CREDIT";
    public const string TransferDebit = "TRANSFER_DEBIT";
    public const string TransferCredit = "TRANSFER_CREDIT";
}

public static class AuditChain
{
    /// <summary>
    /// Checks the records (oldest first, for one wallet) link up and that each hash matches its contents.
    /// Returns the id of the first broken record, or null if the chain is intact.
    /// </summary>
    public static long? Verify(IEnumerable<AuditRecord> recordsOldestFirst)
    {
        var expectedPrevious = AuditRecord.GenesisHash;
        long? previousBalance = null;

        foreach (var record in recordsOldestFirst)
        {
            if (record.PreviousHash != expectedPrevious
                || record.Hash != record.ComputeHash()
                || (previousBalance is not null && record.BalanceBeforeKobo != previousBalance))
            {
                return record.Id;
            }

            expectedPrevious = record.Hash;
            previousBalance = record.BalanceAfterKobo;
        }

        return null;
    }
}
