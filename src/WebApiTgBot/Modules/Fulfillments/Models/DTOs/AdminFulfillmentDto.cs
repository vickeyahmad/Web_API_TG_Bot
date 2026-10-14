namespace WebApiTgBot.Modules.Fulfillments.Models.DTOs;

/// <summary>
/// Safe DTO representing a line item's fulfillment tracking for administrator inspection.
/// </summary>
public class AdminFulfillmentDto
{
    public long FulfillmentId { get; set; }
    public long OrderId { get; set; }
    public long OrderItemId { get; set; }
    public string ProductSku { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public string Currency { get; set; } = "PKR";
    public int QuantityRequested { get; set; }
    public int QuantityFulfilled { get; set; }
    public string Status { get; set; } = string.Empty;
    public string FulfillmentMethod { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public IReadOnlyList<AdminFulfillmentAttemptDto> Attempts { get; set; } = Array.Empty<AdminFulfillmentAttemptDto>();
    public IReadOnlyList<AdminFulfillmentStatusHistoryDto> StatusHistory { get; set; } = Array.Empty<AdminFulfillmentStatusHistoryDto>();
}
