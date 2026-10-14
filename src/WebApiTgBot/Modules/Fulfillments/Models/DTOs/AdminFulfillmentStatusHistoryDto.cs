namespace WebApiTgBot.Modules.Fulfillments.Models.DTOs;

/// <summary>
/// Safe DTO representing a fulfillment status transition audit entry for administrator inspection.
/// </summary>
public class AdminFulfillmentStatusHistoryDto
{
    public long Id { get; set; }
    public long FulfillmentId { get; set; }
    public string? PreviousStatus { get; set; }
    public string NewStatus { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string? ChangedByUserId { get; set; }
    public DateTime ChangedAt { get; set; }
}
