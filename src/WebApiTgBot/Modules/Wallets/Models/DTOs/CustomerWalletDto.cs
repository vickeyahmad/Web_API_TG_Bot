namespace WebApiTgBot.Modules.Wallets.Models.DTOs;

/// <summary>
/// Safe customer-facing wallet summary.
/// </summary>
public class CustomerWalletDto
{
    public decimal Balance { get; set; }
    public string Currency { get; set; } = "PKR";
    public DateTime UpdatedAt { get; set; }
}
