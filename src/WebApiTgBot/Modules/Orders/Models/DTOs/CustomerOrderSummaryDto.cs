namespace WebApiTgBot.Modules.Orders.Models.DTOs;

/// <summary>
/// Lightweight order summary DTO for customer order listing.
/// </summary>
public class CustomerOrderSummaryDto
{
    public string OrderNumber { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Currency { get; set; } = "PKR";
    public decimal Subtotal { get; set; }
    public decimal TotalAmount { get; set; }
    public int ItemCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
