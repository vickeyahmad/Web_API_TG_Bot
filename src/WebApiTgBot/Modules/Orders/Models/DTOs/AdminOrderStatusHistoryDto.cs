namespace WebApiTgBot.Modules.Orders.Models.DTOs;

/// <summary>
/// Administrator audit entry representing an order status transition.
/// </summary>
public class AdminOrderStatusHistoryDto
{
    public long Id { get; set; }
    public string? PreviousStatus { get; set; }
    public string NewStatus { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string? ChangedByUserId { get; set; }
    public DateTime ChangedAt { get; set; }
}
