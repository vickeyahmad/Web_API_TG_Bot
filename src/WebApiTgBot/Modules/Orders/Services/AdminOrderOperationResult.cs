using WebApiTgBot.Modules.Orders.Models.DTOs;

namespace WebApiTgBot.Modules.Orders.Services;

/// <summary>
/// Status codes representing the outcome of an administrative order operation.
/// </summary>
public enum AdminOrderOperationStatus
{
    Success,
    NotFound,
    BadRequest,
    Conflict,
    Forbidden
}

/// <summary>
/// Result wrapper for administrative order operations.
/// </summary>
public class AdminOrderOperationResult
{
    public AdminOrderOperationStatus Status { get; set; }
    public string Message { get; set; } = string.Empty;
    public AdminOrderDetailDto? Data { get; set; }
    public bool IsSuccess => Status == AdminOrderOperationStatus.Success;

    public static AdminOrderOperationResult Success(AdminOrderDetailDto data, string message) => new()
    {
        Status = AdminOrderOperationStatus.Success,
        Data = data,
        Message = message
    };

    public static AdminOrderOperationResult NotFound(string message) => new()
    {
        Status = AdminOrderOperationStatus.NotFound,
        Message = message
    };

    public static AdminOrderOperationResult BadRequest(string message) => new()
    {
        Status = AdminOrderOperationStatus.BadRequest,
        Message = message
    };

    public static AdminOrderOperationResult Conflict(string message) => new()
    {
        Status = AdminOrderOperationStatus.Conflict,
        Message = message
    };

    public static AdminOrderOperationResult Forbidden(string message) => new()
    {
        Status = AdminOrderOperationStatus.Forbidden,
        Message = message
    };
}
