namespace WebApiTgBot.Modules.Wallets.Models.DTOs;

/// <summary>
/// Safe administrative projection of an immutable wallet ledger entry.
/// Returns exact stored balances and transaction metadata without recalculation or mutation.
/// </summary>
public class AdminWalletTransactionDto
{
    /// <summary>Unique transaction primary key identifier.</summary>
    public long TransactionId { get; set; }

    /// <summary>Identifier of the parent wallet.</summary>
    public long WalletId { get; set; }

    /// <summary>Direction of transaction: Credit or Debit.</summary>
    public string TransactionType { get; set; } = string.Empty;

    /// <summary>Positive monetary amount of the transaction in decimal(18,4) precision.</summary>
    public decimal Amount { get; set; }

    /// <summary>Currency code (e.g. PKR).</summary>
    public string Currency { get; set; } = "PKR";

    /// <summary>Wallet balance immediately prior to this transaction.</summary>
    public decimal BalanceBefore { get; set; }

    /// <summary>Wallet balance immediately following this transaction.</summary>
    public decimal BalanceAfter { get; set; }

    /// <summary>Origin category of transaction (e.g. DepositVerification, OrderPayment, Refund, AdminAdjustment).</summary>
    public string SourceType { get; set; } = string.Empty;

    /// <summary>Safe external or internal reference (e.g. OrderNumber, PaymentNumber) when recorded.</summary>
    public string? SourceReferenceId { get; set; }

    /// <summary>Human-readable description or memo.</summary>
    public string? Description { get; set; }

    /// <summary>UTC timestamp when the transaction was appended to the immutable ledger.</summary>
    public DateTime CreatedAt { get; set; }
}
