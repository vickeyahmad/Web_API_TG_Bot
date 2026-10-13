namespace WebApiTgBot.Modules.Payments.Options;

/// <summary>
/// Server-side configuration options for customer wallet order payments.
/// Disabled by default for production safety.
/// </summary>
public class WalletPurchaseOptions
{
    public const string SectionName = "WalletPurchase";

    /// <summary>
    /// Master toggle for paying orders with available wallet balance.
    /// Default is false for production safety.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Supported currency for wallet order purchases (default "PKR").
    /// </summary>
    public string Currency { get; set; } = "PKR";
}
