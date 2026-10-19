namespace WebApiTgBot.Modules.Fulfillments.Models.DTOs;

/// <summary>
/// Safe DTO representing a line item's fulfillment work summary in the administrator work queue.
/// Strictly excludes supplier credentials, raw responses, wholesale costs, customer passwords, and digital secrets.
/// </summary>
public class AdminFulfillmentSummaryDto
{
    /// <summary>
    /// ID of the Fulfillment record in the database, if initialized; otherwise null.
    /// </summary>
    public long? FulfillmentId { get; set; }

    /// <summary>
    /// Whether an explicit Fulfillment record has been created for this line item in the database.
    /// False indicates uninitialized actionable work.
    /// </summary>
    public bool IsInitialized { get; set; }

    /// <summary>
    /// Parent order ID.
    /// </summary>
    public long OrderId { get; set; }

    /// <summary>
    /// Unique human-readable order number (e.g., ORD-20261013-ABC1234).
    /// </summary>
    public string OrderNumber { get; set; } = string.Empty;

    /// <summary>
    /// Line item ID.
    /// </summary>
    public long OrderItemId { get; set; }

    /// <summary>
    /// Customer ID.
    /// </summary>
    public string CustomerId { get; set; } = string.Empty;

    /// <summary>
    /// Customer email address.
    /// </summary>
    public string? CustomerEmail { get; set; }

    /// <summary>
    /// Customer full name.
    /// </summary>
    public string? CustomerName { get; set; }

    /// <summary>
    /// Immutable product SKU snapshot at time of purchase.
    /// </summary>
    public string ProductSku { get; set; } = string.Empty;

    /// <summary>
    /// Immutable product name snapshot at time of purchase.
    /// </summary>
    public string ProductName { get; set; } = string.Empty;

    /// <summary>
    /// Total quantity requested by the customer.
    /// </summary>
    public int QuantityRequested { get; set; }

    /// <summary>
    /// Quantity confirmed fulfilled (0 if unfulfilled or uninitialized).
    /// </summary>
    public int QuantityFulfilled { get; set; }

    /// <summary>
    /// Current fulfillment method (e.g. Unassigned, Manual, VendorApi).
    /// </summary>
    public string FulfillmentMethod { get; set; } = "Unassigned";

    /// <summary>
    /// Persisted FulfillmentStatus from the database if initialized (e.g. NotStarted, InProgress, Completed).
    /// Null if no Fulfillment record has been initialized yet.
    /// </summary>
    public string? FulfillmentStatus { get; set; }

    /// <summary>
    /// Overall queue status representing actionable state in the work queue.
    /// Returns the persisted FulfillmentStatus if initialized, or "Uninitialized" if awaiting initialization.
    /// </summary>
    public string QueueStatus { get; set; } = string.Empty;

    /// <summary>
    /// Current status of the parent order (e.g., Paid, Processing, Completed).
    /// </summary>
    public string OrderStatus { get; set; } = string.Empty;

    /// <summary>
    /// Number of fulfillment attempts recorded.
    /// </summary>
    public int AttemptCount { get; set; }

    /// <summary>
    /// Timestamp when fulfillment or line item was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Timestamp when fulfillment or line item was last updated.
    /// </summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Timestamp when fulfillment was completed, if finished.
    /// </summary>
    public DateTime? CompletedAt { get; set; }
}
