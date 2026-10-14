namespace WebApiTgBot.Modules.Fulfillments.Models.DTOs;

/// <summary>
/// Status codes representing the outcome of an administrative manual fulfillment operation.
/// </summary>
public enum ManualFulfillmentOperationStatus
{
    Success,
    NotFound,
    BadRequest,
    Conflict,
    Forbidden
}

/// <summary>
/// Result wrapper for administrative manual fulfillment operations.
/// </summary>
public class ManualFulfillmentOperationResult
{
    public ManualFulfillmentOperationStatus Status { get; set; }
    public string Message { get; set; } = string.Empty;
    public AdminFulfillmentDto? Data { get; set; }
    public bool IsSuccess => Status == ManualFulfillmentOperationStatus.Success;

    public static ManualFulfillmentOperationResult Success(AdminFulfillmentDto data, string message) => new()
    {
        Status = ManualFulfillmentOperationStatus.Success,
        Data = data,
        Message = message
    };

    public static ManualFulfillmentOperationResult NotFound(string message) => new()
    {
        Status = ManualFulfillmentOperationStatus.NotFound,
        Message = message
    };

    public static ManualFulfillmentOperationResult BadRequest(string message) => new()
    {
        Status = ManualFulfillmentOperationStatus.BadRequest,
        Message = message
    };

    public static ManualFulfillmentOperationResult Conflict(string message) => new()
    {
        Status = ManualFulfillmentOperationStatus.Conflict,
        Message = message
    };

    public static ManualFulfillmentOperationResult Forbidden(string message) => new()
    {
        Status = ManualFulfillmentOperationStatus.Forbidden,
        Message = message
    };
}
