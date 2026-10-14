using WebApiTgBot.Modules.Fulfillments.Models;

namespace WebApiTgBot.Modules.Fulfillments.Models.DTOs;

public class FulfillmentAttemptDto
{
    public long Id { get; set; }
    public long FulfillmentId { get; set; }
    public int AttemptNumber { get; set; }
    public string AttemptKey { get; set; } = string.Empty;
    public int? VendorProductId { get; set; }
    public FulfillmentAttemptStatus Status { get; set; }
    public string? ExternalVendorOrderId { get; set; }
    public FulfillmentFailureCategory? FailureCategory { get; set; }
    public string? SafeErrorSummary { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
