using WebApiTgBot.Modules.Fulfillments.Models;

namespace WebApiTgBot.Modules.Fulfillments.Models.DTOs;

public class FulfillmentSummaryDto
{
    public long Id { get; set; }
    public long OrderId { get; set; }
    public long OrderItemId { get; set; }
    public FulfillmentStatus Status { get; set; }
    public FulfillmentMethod FulfillmentMethod { get; set; }
    public int QuantityRequested { get; set; }
    public int QuantityFulfilled { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int AttemptCount { get; set; }
}
