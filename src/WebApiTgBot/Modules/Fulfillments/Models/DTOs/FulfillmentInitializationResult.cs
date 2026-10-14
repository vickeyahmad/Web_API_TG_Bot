using WebApiTgBot.Modules.Fulfillments.Models;

namespace WebApiTgBot.Modules.Fulfillments.Models.DTOs;

public enum FulfillmentInitializationStatus
{
    Success = 1,
    AlreadyInitialized = 2,
    OrderNotFound = 3,
    OrderItemNotFound = 4,
    UnpaidOrderRejected = 5,
    InvalidOrderState = 6,
    PaymentMismatch = 7
}

public class FulfillmentInitializationResult
{
    public FulfillmentInitializationStatus Status { get; set; }
    public string Message { get; set; } = string.Empty;
    public Fulfillment? Fulfillment { get; set; }

    public static FulfillmentInitializationResult Succeeded(Fulfillment fulfillment) =>
        new() { Status = FulfillmentInitializationStatus.Success, Message = "Fulfillment initialized successfully.", Fulfillment = fulfillment };

    public static FulfillmentInitializationResult AlreadyExists(Fulfillment fulfillment) =>
        new() { Status = FulfillmentInitializationStatus.AlreadyInitialized, Message = "Fulfillment is already initialized for this order item.", Fulfillment = fulfillment };

    public static FulfillmentInitializationResult Failed(FulfillmentInitializationStatus status, string message) =>
        new() { Status = status, Message = message };
}
