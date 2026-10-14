namespace WebApiTgBot.Modules.Fulfillments.Models;

/// <summary>
/// Categorization of fulfillment attempt failures.
/// Used for diagnostics, metrics, and safe supplier fallback decisions.
/// </summary>
public enum FulfillmentFailureCategory
{
    None = 0,
    VendorOutOfStock = 1,
    VendorSystemError = 2,
    InvalidCustomerInput = 3,
    NetworkTimeout = 4,
    InsufficientVendorBalance = 5,
    Other = 99
}
