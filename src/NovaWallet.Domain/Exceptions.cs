namespace NovaWallet.Domain;

/// <summary>
/// A business-rule failure. <see cref="Code"/> is a stable, machine-readable identifier that the API
/// maps to an HTTP status and a Problem Details <c>type</c>; clients should branch on it, not on the message.
/// </summary>
public class DomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class ErrorCodes
{
    public const string InvalidAmount = "invalid_amount";
    public const string InsufficientFunds = "insufficient_funds";
    public const string DailyLimitExceeded = "daily_limit_exceeded";
    public const string WalletNotFound = "wallet_not_found";
    public const string WalletAlreadyExists = "wallet_already_exists";
    public const string SameWalletTransfer = "same_wallet_transfer";
    public const string IdempotencyKeyReused = "idempotency_key_reused";
    public const string DuplicateReference = "duplicate_reference";
    public const string Forbidden = "forbidden";
    public const string Validation = "validation_error";
    public const string InvalidCredentials = "invalid_credentials";
    public const string EmailAlreadyRegistered = "email_already_registered";
    public const string InvalidRefreshToken = "invalid_refresh_token";
    public const string WalletFrozen = "wallet_frozen";
    public const string UserNotFound = "user_not_found";
    public const string AdminRuleViolation = "admin_rule_violation";
}

public sealed class InvalidAmountException(string message)
    : DomainException(ErrorCodes.InvalidAmount, message);

public sealed class InsufficientFundsException()
    : DomainException(ErrorCodes.InsufficientFunds, "The source wallet does not have enough funds for this transfer.");

public sealed class DailyLimitExceededException(Money limit, Money remaining)
    : DomainException(ErrorCodes.DailyLimitExceeded,
        $"This transfer exceeds the daily outbound limit of {limit}. Remaining today: {remaining}.");

public sealed class WalletNotFoundException(Guid walletId)
    : DomainException(ErrorCodes.WalletNotFound, $"Wallet '{walletId}' was not found.");

public sealed class WalletAlreadyExistsException()
    : DomainException(ErrorCodes.WalletAlreadyExists, "This customer already has a wallet.");

public sealed class SameWalletTransferException()
    : DomainException(ErrorCodes.SameWalletTransfer, "Source and destination wallets must be different.");

public sealed class IdempotencyKeyReusedException()
    : DomainException(ErrorCodes.IdempotencyKeyReused,
        "This Idempotency-Key was already used with a different request payload.");

public sealed class DuplicateReferenceException(string reference)
    : DomainException(ErrorCodes.DuplicateReference,
        $"Reference '{reference}' was already used for a different credit.");

public sealed class ForbiddenException(string message)
    : DomainException(ErrorCodes.Forbidden, message);

public sealed class RequestValidationException(string message)
    : DomainException(ErrorCodes.Validation, message);

public sealed class WalletFrozenException()
    : DomainException(ErrorCodes.WalletFrozen, "This wallet is on hold and cannot send money. Contact support.");

/// <summary>Deliberately identical for unknown email, wrong password, disabled or locked account.</summary>
public sealed class InvalidCredentialsException()
    : DomainException(ErrorCodes.InvalidCredentials, "The email or password is incorrect.");

public sealed class EmailAlreadyRegisteredException()
    : DomainException(ErrorCodes.EmailAlreadyRegistered, "An account with this email already exists.");

public sealed class InvalidRefreshTokenException()
    : DomainException(ErrorCodes.InvalidRefreshToken, "The refresh token is invalid, expired or revoked. Sign in again.");

public sealed class UserNotFoundException(Guid userId)
    : DomainException(ErrorCodes.UserNotFound, $"User '{userId}' was not found.");

public sealed class AdminRuleViolationException(string message)
    : DomainException(ErrorCodes.AdminRuleViolation, message);

/// <summary>
/// Re-raised when an idempotent request is replayed and the original attempt was rejected,
/// so the client gets the same error it got the first time.
/// </summary>
public sealed class ReplayedRejectionException(string code, string message)
    : DomainException(code, message);
