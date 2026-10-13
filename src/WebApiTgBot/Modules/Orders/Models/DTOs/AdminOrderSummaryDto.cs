namespace WebApiTgBot.Modules.Orders.Models.DTOs;

/// <summary>
/// Administrator summary DTO for paginated order listings.
/// Provides essential customer, product, and financial context across all sales channels.
/// </summary>
public class AdminOrderSummaryDto
{
    public string OrderNumber { get; set; } = string.Empty;
    public long OrderId { get; set; }
    public string CustomerId { get; set; } = string.Empty;
    public string? CustomerEmail { get; set; }
    public string? CustomerName { get; set; }
    public string ProductSummary { get; set; } = string.Empty;
    public int ItemCount { get; set; }
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = "PKR";
    public string Status { get; set; } = string.Empty;
    public string SalesChannel { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public AdminOrderPaymentSummaryDto? PaymentSummary { get; set; }
}
