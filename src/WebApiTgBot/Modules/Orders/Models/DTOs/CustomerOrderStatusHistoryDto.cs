namespace WebApiTgBot.Modules.Orders.Models.DTOs;

/// <summary>
/// Customer-visible order status transition event.
/// Excludes internal staff user IDs and internal system logs.
/// </summary>
public class CustomerOrderStatusHistoryDto
{
    public string Status { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public DateTime ChangedAt { get; set; }
}
