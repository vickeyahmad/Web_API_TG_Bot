namespace WebApiTgBot.Modules.Payments.Options;

/// <summary>
/// Server-side configuration options for customer manual wallet top-ups.
/// Disabled by default for production safety.
/// </summary>
public class WalletTopUpOptions
{
    public const string SectionName = "WalletTopUp";

    /// <summary>
    /// Master toggle for the wallet top-up feature.
    /// Default is false for production safety.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Minimum allowed top-up amount in PKR.
    /// </summary>
    public decimal MinAmount { get; set; } = 100.00m;

    /// <summary>
    /// Maximum allowed top-up amount in PKR per transaction.
    /// </summary>
    public decimal MaxAmount { get; set; } = 500000.00m;

    /// <summary>
    /// Default currency for manual top-ups.
    /// </summary>
    public string Currency { get; set; } = "PKR";

    /// <summary>
    /// Supported manual payment method codes.
    /// </summary>
    public string[] AllowedMethodCodes { get; set; } = new[]
    {
        "BANK_TRANSFER",
        "MANUAL_DEPOSIT",
        "JAZZCASH_MANUAL",
        "EASYPAISA_MANUAL"
    };

    /// <summary>
    /// Receiving bank name presented to customers.
    /// </summary>
    public string BankName { get; set; } = "Meezan Bank";

    /// <summary>
    /// Receiving account title.
    /// </summary>
    public string AccountTitle { get; set; } = "Gogaabyte Technologies";

    /// <summary>
    /// Receiving account or IBAN number.
    /// </summary>
    public string AccountNumber { get; set; } = "01020304050607";

    /// <summary>
    /// Guidance instructions presented to the customer.
    /// </summary>
    public string Instructions { get; set; } = "Transfer funds to the specified account and submit your Transaction Reference ID for verification.";
}
