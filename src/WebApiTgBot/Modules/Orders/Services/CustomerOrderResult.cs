using WebApiTgBot.Modules.Orders.Models.DTOs;

namespace WebApiTgBot.Modules.Orders.Services;

/// <summary>
/// Status codes representing customer order operations.
/// </summary>
public enum CustomerOrderResultStatus
{
    Success,
    NotFound,
    Invalid,
    Conflict,
    Forbidden
}

/// <summary>
/// Result container for customer order creation operations.
/// </summary>
public class CustomerOrderResult
{
    public CustomerOrderResultStatus Status { get; init; }
    public string Message { get; init; } = string.Empty;
    public CustomerOrderDetailDto? Order { get; init; }
    public bool IsExistingIdempotent { get; init; }

    public static CustomerOrderResult Ok(CustomerOrderDetailDto order, string message = "Order created successfully.", bool isExistingIdempotent = false) =>
        new() { Status = CustomerOrderResultStatus.Success, Order = order, Message = message, IsExistingIdempotent = isExistingIdempotent };

    public static CustomerOrderResult NotFound(string message = "The requested product or order was not found.") =>
        new() { Status = CustomerOrderResultStatus.NotFound, Message = message };

    public static CustomerOrderResult Invalid(string message = "Invalid order request.") =>
        new() { Status = CustomerOrderResultStatus.Invalid, Message = message };

    public static CustomerOrderResult Conflict(string message = "An order conflict occurred.") =>
        new() { Status = CustomerOrderResultStatus.Conflict, Message = message };

    public static CustomerOrderResult Forbidden(string message = "You do not have access to this resource.") =>
        new() { Status = CustomerOrderResultStatus.Forbidden, Message = message };
}
