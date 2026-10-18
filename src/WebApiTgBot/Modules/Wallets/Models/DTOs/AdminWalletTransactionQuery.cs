using WebApiTgBot.Modules.Wallets.Models;

namespace WebApiTgBot.Modules.Wallets.Models.DTOs;

/// <summary>
/// Query parameters for filtering and paginating wallet transaction history.
/// </summary>
public class AdminWalletTransactionQuery
{
    /// <summary>Optional filter by transaction direction (Credit, Debit).</summary>
    public WalletTransactionType? TransactionType { get; set; }

    /// <summary>Optional filter by source category (e.g. DepositVerification, OrderPayment, Refund, AdminAdjustment).</summary>
    public string? SourceType { get; set; }

    /// <summary>Page number (1-based, default: 1).</summary>
    public int Page { get; set; } = 1;

    /// <summary>Page size (default: 20, max: 100).</summary>
    public int PageSize { get; set; } = 20;
}
