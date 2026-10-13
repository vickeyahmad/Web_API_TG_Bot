namespace WebApiTgBot.Modules.Payments.Models;

/// <summary>
/// Purpose and target domain entity of a central payment attempt.
/// </summary>
public enum PaymentPurpose
{
    /// <summary>
    /// Payment intended to fund an order purchase (linked to an Order).
    /// </summary>
    OrderPurchase = 1,

    /// <summary>
    /// Payment intended to fund a customer wallet top-up (linked to a Wallet).
    /// </summary>
    WalletTopUp = 2
}
