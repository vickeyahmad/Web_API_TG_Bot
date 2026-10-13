namespace WebApiTgBot.Modules.Payments.Models.DTOs;

public enum WalletOrderPaymentStatus
{
    Success,
    BadRequest,
    Unauthorized,
    Forbidden,
    NotFound,
    InsufficientFunds,
    Conflict
}

public class WalletOrderPaymentResult
{
    public WalletOrderPaymentStatus Status { get; set; }
    public string Message { get; set; } = string.Empty;
    public CustomerOrderPaymentDto? Data { get; set; }
    public bool IsReplay { get; set; }

    public static WalletOrderPaymentResult Success(CustomerOrderPaymentDto data, string message = "Order paid successfully.", bool isReplay = false) =>
        new() { Status = WalletOrderPaymentStatus.Success, Data = data, Message = message, IsReplay = isReplay };

    public static WalletOrderPaymentResult BadRequest(string message) =>
        new() { Status = WalletOrderPaymentStatus.BadRequest, Message = message };

    public static WalletOrderPaymentResult Unauthorized(string message) =>
        new() { Status = WalletOrderPaymentStatus.Unauthorized, Message = message };

    public static WalletOrderPaymentResult Forbidden(string message) =>
        new() { Status = WalletOrderPaymentStatus.Forbidden, Message = message };

    public static WalletOrderPaymentResult NotFound(string message) =>
        new() { Status = WalletOrderPaymentStatus.NotFound, Message = message };

    public static WalletOrderPaymentResult InsufficientFunds(string message) =>
        new() { Status = WalletOrderPaymentStatus.InsufficientFunds, Message = message };

    public static WalletOrderPaymentResult Conflict(string message) =>
        new() { Status = WalletOrderPaymentStatus.Conflict, Message = message };
}
