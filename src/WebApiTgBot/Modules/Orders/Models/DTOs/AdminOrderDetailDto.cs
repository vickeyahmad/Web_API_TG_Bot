namespace WebApiTgBot.Modules.Orders.Models.DTOs;

/// <summary>
/// Detailed administrator order representation.
/// Combines customer identity, historical product item snapshots, financial totals,
/// order status history, and associated OrderPurchase payment attempts.
/// </summary>
public class AdminOrderDetailDto
{
    public string OrderNumber { get; set; } = string.Empty;
    public long OrderId { get; set; }
    public string CustomerId { get; set; } = string.Empty;
    public string? CustomerEmail { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerPhoneNumber { get; set; }
    public string SalesChannel { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public decimal Subtotal { get; set; }
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = "PKR";
    public string? CustomerNotes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public List<AdminOrderItemDto> Items { get; set; } = new();
    public List<AdminOrderStatusHistoryDto> StatusHistory { get; set; } = new();
    public List<AdminOrderPaymentDto> Payments { get; set; } = new();
}
