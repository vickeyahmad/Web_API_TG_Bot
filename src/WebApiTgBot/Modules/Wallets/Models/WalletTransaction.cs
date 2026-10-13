using WebApiTgBot.Modules.Identity.Models;

namespace WebApiTgBot.Modules.Wallets.Models;

/// <summary>
/// Permanent, append-only financial ledger entry recording every wallet balance mutation.
/// Historical records are never modified or deleted; corrections require new compensating entries.
/// Guaranteed to match BalanceBefore + (Credit ? Amount : -Amount) == BalanceAfter.
/// </summary>
public class WalletTransaction
{
    public long Id { get; set; }

    /// <summary>
    /// Foreign key linking to the parent Wallet.
    /// </summary>
    public long WalletId { get; set; }

    public Wallet? Wallet { get; set; }

    /// <summary>
    /// Type of mutation: Credit (increases balance) or Debit (decreases balance).
    /// </summary>
    public WalletTransactionType TransactionType { get; set; }

    /// <summary>
    /// Positive monetary amount of this transaction in decimal(18,4) precision.
    /// Strictly greater than zero.
    /// </summary>
    public decimal Amount { get; set; }

    /// <summary>
    /// Currency code (e.g. "PKR"). Cross-currency transactions are prohibited.
    /// </summary>
    public string Currency { get; set; } = "PKR";

    /// <summary>
    /// Wallet balance immediately prior to this transaction.
    /// </summary>
    public decimal BalanceBefore { get; set; }

    /// <summary>
    /// Wallet balance immediately following this transaction.
    /// </summary>
    public decimal BalanceAfter { get; set; }

    /// <summary>
    /// Durable idempotency key supplied by internal caller to guarantee exactly-once processing.
    /// Unique per wallet in PostgreSQL.
    /// </summary>
    public string OperationKey { get; set; } = string.Empty;

    /// <summary>
    /// Category identifying the origin/reason of the transaction (e.g. DepositVerification, OrderPayment).
    /// </summary>
    public string SourceType { get; set; } = string.Empty;

    /// <summary>
    /// Optional external or internal identifier linking to the underlying event (e.g. OrderNumber, PaymentNumber).
    /// </summary>
    public string? SourceReferenceId { get; set; }

    /// <summary>
    /// Optional human-readable memo or description.
    /// </summary>
    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// User ID of the administrator or system actor that authorized the transaction.
    /// Nullable; preserves attribution without cascading delete.
    /// </summary>
    public string? CreatedByUserId { get; set; }

    public ApplicationUser? CreatedByUser { get; set; }
}
