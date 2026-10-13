namespace WebApiTgBot.Modules.Wallets.Services;

/// <summary>
/// Parameter object for an internal wallet credit operation.
/// </summary>
public class WalletCreditRequest
{
    /// <summary>Customer identifier owning the wallet to credit.</summary>
    public string CustomerId { get; set; } = string.Empty;

    /// <summary>Monetary amount to credit. Must be strictly positive.</summary>
    public decimal Amount { get; set; }

    /// <summary>Currency code (default "PKR").</summary>
    public string Currency { get; set; } = "PKR";

    /// <summary>Mandatory durable idempotency key to prevent double credits.</summary>
    public string OperationKey { get; set; } = string.Empty;

    /// <summary>Category identifying the reason/source of this credit (see WalletTransactionSource).</summary>
    public string SourceType { get; set; } = string.Empty;

    /// <summary>Optional reference ID (e.g. verified payment number or order number).</summary>
    public string? SourceReferenceId { get; set; }

    /// <summary>Optional descriptive memo.</summary>
    public string? Description { get; set; }

    /// <summary>Optional ID of the initiating administrator or system actor.</summary>
    public string? CreatedByUserId { get; set; }
}
