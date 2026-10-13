namespace WebApiTgBot.Modules.Wallets.Models.DTOs;

/// <summary>
/// Safe customer-facing transaction ledger entry.
/// Omits internal operator identities and system credentials.
/// </summary>
public class CustomerWalletTransactionDto
{
    public long Id { get; set; }
    public string TransactionType { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "PKR";
    public decimal BalanceBefore { get; set; }
    public decimal BalanceAfter { get; set; }
    public string SourceType { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
}
