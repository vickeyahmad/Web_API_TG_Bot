using WebApiTgBot.Modules.Wallets.Models;

namespace WebApiTgBot.Modules.Wallets.Services;

/// <summary>
/// Result returned by internal wallet mutation operations.
/// </summary>
public class WalletOperationResult
{
    public WalletOperationStatus Status { get; set; }
    public string Message { get; set; } = string.Empty;
    public Wallet? Wallet { get; set; }
    public WalletTransaction? Transaction { get; set; }
    public bool IsReplay { get; set; }

    public bool IsSuccess => Status == WalletOperationStatus.Success;

    public static WalletOperationResult Succeeded(Wallet wallet, WalletTransaction transaction, bool isReplay = false)
    {
        return new WalletOperationResult
        {
            Status = WalletOperationStatus.Success,
            Message = isReplay ? "Operation previously completed (idempotent replay)." : "Operation completed successfully.",
            Wallet = wallet,
            Transaction = transaction,
            IsReplay = isReplay
        };
    }

    public static WalletOperationResult InsufficientFunds(decimal available, decimal requested, string currency)
    {
        return new WalletOperationResult
        {
            Status = WalletOperationStatus.InsufficientFunds,
            Message = $"Insufficient wallet balance. Available: {available:F4} {currency}, Requested: {requested:F4} {currency}."
        };
    }

    public static WalletOperationResult Conflict(string message)
    {
        return new WalletOperationResult
        {
            Status = WalletOperationStatus.Conflict,
            Message = message
        };
    }

    public static WalletOperationResult NotFound(string message)
    {
        return new WalletOperationResult
        {
            Status = WalletOperationStatus.NotFound,
            Message = message
        };
    }

    public static WalletOperationResult InvalidAmount(string message)
    {
        return new WalletOperationResult
        {
            Status = WalletOperationStatus.InvalidAmount,
            Message = message
        };
    }

    public static WalletOperationResult InvalidCurrency(string message)
    {
        return new WalletOperationResult
        {
            Status = WalletOperationStatus.InvalidCurrency,
            Message = message
        };
    }

    public static WalletOperationResult Failed(string message)
    {
        return new WalletOperationResult
        {
            Status = WalletOperationStatus.Failed,
            Message = message
        };
    }
}
