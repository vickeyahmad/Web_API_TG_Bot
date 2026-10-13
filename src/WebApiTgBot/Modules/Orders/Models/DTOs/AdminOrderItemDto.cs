namespace WebApiTgBot.Modules.Orders.Models.DTOs;

/// <summary>
/// Administrator representation of an order line item.
/// Uses historical price, name, and SKU snapshots rather than current catalog values.
/// </summary>
public class AdminOrderItemDto
{
    public string ProductSku { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal TotalPrice { get; set; }
    public string Currency { get; set; } = "PKR";
}
