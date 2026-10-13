namespace WebApiTgBot.Modules.Payments.Models.DTOs;

public enum WalletTopUpResultStatus
{
    Success,
    FeatureDisabled,
    BadRequest,
    NotFound,
    Conflict
}

public class WalletTopUpOperationResult
{
    public WalletTopUpResultStatus Status { get; set; }
    public string Message { get; set; } = string.Empty;
    public CustomerWalletTopUpDto? TopUp { get; set; }
    public bool IsExistingIdempotent { get; set; }

    public static WalletTopUpOperationResult Succeeded(CustomerWalletTopUpDto dto, string message = "Success", bool isExisting = false) =>
        new() { Status = WalletTopUpResultStatus.Success, TopUp = dto, Message = message, IsExistingIdempotent = isExisting };

    public static WalletTopUpOperationResult FeatureDisabled(string message = "Wallet top-up service is currently disabled.") =>
        new() { Status = WalletTopUpResultStatus.FeatureDisabled, Message = message };

    public static WalletTopUpOperationResult BadRequest(string message) =>
        new() { Status = WalletTopUpResultStatus.BadRequest, Message = message };

    public static WalletTopUpOperationResult NotFound(string message) =>
        new() { Status = WalletTopUpResultStatus.NotFound, Message = message };

    public static WalletTopUpOperationResult Conflict(string message) =>
        new() { Status = WalletTopUpResultStatus.Conflict, Message = message };
}
