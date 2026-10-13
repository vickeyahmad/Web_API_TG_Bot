namespace WebApiTgBot.Modules.Wallets.Services;

/// <summary>
/// Parameter object for an internal wallet debit operation.
/// </summary>
public class WalletDebitRequest
{
    /// <summary>Customer identifier owning the wallet to debit.</summary>
    public string CustomerId { get; set; } = string.Empty;

    /// <summary>Monetary amount to debit. Must be strictly positive.</summary>
    public decimal Amount { get; set; }

    /// <summary>Currency code (default "PKR").</summary>
    public string Currency { get; set; } = "PKR";

    /// <summary>Mandatory durable idempotency key to prevent double debits.</summary>
    public string OperationKey { get; set; } = string.Empty;

    /// <summary>Category identifying the reason/source of this debit (see WalletTransactionSource).</summary>
    public string SourceType { get; set; } = string.Empty;

    /// <summary>Optional reference ID (e.g. order number being paid).</summary>
    public string? SourceReferenceId { get; set; }

    /// <summary>Optional descriptive memo.</summary>
    public string? Description { get; set; }

    /// <summary>Optional ID of the initiating administrator or customer session.</summary>
    public string? CreatedByUserId { get; set; }
}
