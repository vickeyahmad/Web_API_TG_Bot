namespace WebApiTgBot.Modules.Orders.Models.DTOs;

/// <summary>
/// Safe representation of an order line item for customer responses.
/// Captures historical snapshot of the purchased product without exposing supplier or cost data.
/// </summary>
public class CustomerOrderItemDto
{
    public long Id { get; set; }
    public string ProductSku { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; }
    public decimal TotalPrice { get; set; }
    public string Currency { get; set; } = "PKR";
}
