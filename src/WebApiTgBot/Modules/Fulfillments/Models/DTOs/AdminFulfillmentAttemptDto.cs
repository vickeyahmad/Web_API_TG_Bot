namespace WebApiTgBot.Modules.Fulfillments.Models.DTOs;

/// <summary>
/// Safe DTO representing an individual fulfillment attempt for administrator inspection.
/// Strictly excludes supplier credentials, raw responses, and digital secrets.
/// </summary>
public class AdminFulfillmentAttemptDto
{
    public long AttemptId { get; set; }
    public int AttemptNumber { get; set; }
    public string AttemptKey { get; set; } = string.Empty;
    public bool IsManual => !VendorProductId.HasValue;
    public int? VendorProductId { get; set; }
    public int? VendorId { get; set; }
    public string? VendorName { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? ExternalVendorOrderId { get; set; }
    public string? FailureCategory { get; set; }
    public string? SafeErrorSummary { get; set; }
    public string? DeliveryReference { get; set; }
    public string? DeliveryChannel { get; set; }
    public string? Notes { get; set; }
    public string? InitiatedByUserId { get; set; }
    public string? ConfirmedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
