namespace WebApiTgBot.Modules.Orders.Models.DTOs;

/// <summary>
/// Comprehensive, safe order detail DTO for customer order tracking.
/// Includes immutable product snapshots and customer-visible status history.
/// Strictly excludes supplier identities, vendor costs, and backend routing data.
/// </summary>
public class CustomerOrderDetailDto
{
    public string OrderNumber { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Currency { get; set; } = "PKR";
    public decimal Subtotal { get; set; }
    public decimal TotalAmount { get; set; }
    public string SalesChannel { get; set; } = string.Empty;
    public string? CustomerNotes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public IReadOnlyList<CustomerOrderItemDto> Items { get; set; } = Array.Empty<CustomerOrderItemDto>();
    public IReadOnlyList<CustomerOrderStatusHistoryDto> StatusHistory { get; set; } = Array.Empty<CustomerOrderStatusHistoryDto>();
}
