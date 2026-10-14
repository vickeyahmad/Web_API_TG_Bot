using WebApiTgBot.Modules.Fulfillments.Models;

namespace WebApiTgBot.Modules.Fulfillments.Models.DTOs;

public enum FulfillmentAttemptOperationStatus
{
    Success = 1,
    FulfillmentNotFound = 2,
    InvalidVendorProduct = 3,
    AttemptAlreadyTerminal = 4,
    FallbackNotAllowedDueToUnconfirmedOutcome = 5,
    DuplicateAttemptKey = 6,
    InvalidAttemptStatus = 7
}

public class FulfillmentAttemptResult
{
    public FulfillmentAttemptOperationStatus Status { get; set; }
    public string Message { get; set; } = string.Empty;
    public FulfillmentAttempt? Attempt { get; set; }

    public static FulfillmentAttemptResult Succeeded(FulfillmentAttempt attempt) =>
        new() { Status = FulfillmentAttemptOperationStatus.Success, Message = "Attempt operation completed successfully.", Attempt = attempt };

    public static FulfillmentAttemptResult Failed(FulfillmentAttemptOperationStatus status, string message) =>
        new() { Status = status, Message = message };
}
