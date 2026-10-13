namespace WebApiTgBot.Modules.Wallets.Models;

/// <summary>
/// Standardized category identifiers for wallet transaction ledger entries.
/// Provides traceability for every balance mutation.
/// </summary>
public static class WalletTransactionSource
{
    /// <summary>Balance credited through verified payment gateway or approved manual receipt.</summary>
    public const string DepositVerification = "DepositVerification";

    /// <summary>Balance debited to pay for a customer order.</summary>
    public const string OrderPayment = "OrderPayment";

    /// <summary>Balance credited as a refund for a cancelled or failed order.</summary>
    public const string Refund = "Refund";

    /// <summary>Traceable manual balance adjustment performed by an administrator.</summary>
    public const string AdminAdjustment = "AdminAdjustment";

    /// <summary>Traceable compensating correction transaction.</summary>
    public const string CompensatingCorrection = "CompensatingCorrection";

    private static readonly HashSet<string> ValidSources = new(StringComparer.OrdinalIgnoreCase)
    {
        DepositVerification,
        OrderPayment,
        Refund,
        AdminAdjustment,
        CompensatingCorrection
    };

    public static bool IsValid(string? source)
    {
        return !string.IsNullOrWhiteSpace(source) && ValidSources.Contains(source.Trim());
    }
}
