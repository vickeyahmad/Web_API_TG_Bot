namespace WebApiTgBot.Modules.Fulfillments.Models.DTOs;

/// <summary>
/// Query parameters for searching, filtering, and paginating the administrator fulfillment work queue.
/// </summary>
public class AdminFulfillmentQuery
{
    /// <summary>
    /// Current page number (1-based, default: 1).
    /// </summary>
    public int Page { get; set; } = 1;

    /// <summary>
    /// Number of items per page (default: 20, max: 100).
    /// </summary>
    public int PageSize { get; set; } = 20;

    /// <summary>
    /// Optional filter by queue status or fulfillment status.
    /// Supported values: "Uninitialized", or any FulfillmentStatus ("NotStarted", "Pending", "InProgress", "Completed", "PartiallyCompleted", "Failed", "RequiresReview").
    /// </summary>
    public string? Status { get; set; }

    /// <summary>
    /// Optional filter by fulfillment method (e.g. "Unassigned", "Manual", "VendorApi").
    /// </summary>
    public string? FulfillmentMethod { get; set; }

    /// <summary>
    /// Optional filter by parent order status (e.g. "Paid", "Processing", "Completed").
    /// </summary>
    public string? OrderStatus { get; set; }

    /// <summary>
    /// Optional search term matching order number, customer name, customer email, product name, or product SKU snapshot.
    /// </summary>
    public string? Search { get; set; }
}
