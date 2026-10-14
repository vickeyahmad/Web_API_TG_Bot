namespace WebApiTgBot.Modules.Fulfillments.Models.DTOs;

/// <summary>
/// Overview of all line item fulfillments for an order for administrator inspection.
/// </summary>
public class AdminOrderFulfillmentsDto
{
    public string OrderNumber { get; set; } = string.Empty;
    public long OrderId { get; set; }
    public string OrderStatus { get; set; } = string.Empty;
    public IReadOnlyList<AdminFulfillmentDto> Fulfillments { get; set; } = Array.Empty<AdminFulfillmentDto>();
}
