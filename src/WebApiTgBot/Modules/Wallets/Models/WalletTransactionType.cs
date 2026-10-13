namespace WebApiTgBot.Modules.Wallets.Models;

/// <summary>
/// Direction of a wallet balance mutation in the permanent transaction ledger.
/// </summary>
public enum WalletTransactionType
{
    /// <summary>
    /// Increases the wallet balance (e.g. verified deposit, refund, positive adjustment).
    /// </summary>
    Credit = 1,

    /// <summary>
    /// Decreases the wallet balance (e.g. order purchase, negative adjustment).
    /// </summary>
    Debit = 2
}
