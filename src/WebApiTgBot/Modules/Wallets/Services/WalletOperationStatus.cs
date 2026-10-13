namespace WebApiTgBot.Modules.Wallets.Services;

/// <summary>
/// Status codes returned by internal wallet mutation operations.
/// </summary>
public enum WalletOperationStatus
{
    /// <summary>Operation completed successfully and produced a durable ledger transaction.</summary>
    Success = 1,

    /// <summary>Requested debit exceeds the current available wallet balance.</summary>
    InsufficientFunds = 2,

    /// <summary>Operation key was already used for a conflicting transaction with different parameters.</summary>
    Conflict = 3,

    /// <summary>Referenced wallet or customer could not be found.</summary>
    NotFound = 4,

    /// <summary>Amount was zero, negative, or invalid.</summary>
    InvalidAmount = 5,

    /// <summary>Currency mismatch between request and wallet.</summary>
    InvalidCurrency = 6,

    /// <summary>Customer account is inactive or disabled.</summary>
    CustomerInactive = 7,

    /// <summary>Internal failure or database constraint violation occurred.</summary>
    Failed = 8
}
