namespace WebApiTgBot.Modules.Wallets.Models.DTOs;

/// <summary>
/// Safe administrative overview projection of a customer wallet.
/// Displays authoritative balance without recalculation or mutation.
/// </summary>
public class AdminWalletSummaryDto
{
    /// <summary>Unique wallet primary key identifier.</summary>
    public long WalletId { get; set; }

    /// <summary>Foreign key linking to customer ApplicationUser identity.</summary>
    public string CustomerId { get; set; } = string.Empty;

    /// <summary>Customer full name when available.</summary>
    public string? CustomerFullName { get; set; }

    /// <summary>Customer email address when available.</summary>
    public string? CustomerEmail { get; set; }

    /// <summary>Authoritative current wallet balance stored with decimal(18,4) precision.</summary>
    public decimal CurrentBalance { get; set; }

    /// <summary>Currency code (e.g. PKR).</summary>
    public string Currency { get; set; } = "PKR";

    /// <summary>UTC timestamp when the wallet was initialized.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>UTC timestamp when the wallet was last updated.</summary>
    public DateTime UpdatedAt { get; set; }
}
