using WebApiTgBot.Modules.Identity.Models;

namespace WebApiTgBot.Modules.Wallets.Models;

/// <summary>
/// Central customer wallet entity storing available funds per currency.
/// Strictly decoupled from individual sales channels and shared across Website, Telegram, and SMM.
/// Never modified directly by client requests; mutated exclusively via internal IWalletService.
/// </summary>
public class Wallet
{
    public long Id { get; set; }

    /// <summary>
    /// Foreign key linking to the central ApplicationUser identity system.
    /// A customer has at most one wallet per currency.
    /// </summary>
    public string CustomerId { get; set; } = string.Empty;

    public ApplicationUser? Customer { get; set; }

    /// <summary>
    /// ISO 4217 currency code (default "PKR").
    /// </summary>
    public string Currency { get; set; } = "PKR";

    /// <summary>
    /// Current available wallet balance stored with exact decimal(18,4) precision.
    /// Must never be negative. Initialized to zero.
    /// </summary>
    public decimal Balance { get; set; } = 0.0000m;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Permanent ledger of all historical credit and debit transactions for this wallet.
    /// </summary>
    public ICollection<WalletTransaction> Transactions { get; set; } = new List<WalletTransaction>();
}
