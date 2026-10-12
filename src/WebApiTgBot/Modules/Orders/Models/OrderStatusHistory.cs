using WebApiTgBot.Modules.Identity.Models;

namespace WebApiTgBot.Modules.Orders.Models;

/// <summary>
/// Immutable audit entry recording an order status transition.
/// Every status change must create a permanent entry here.
/// Silently overwriting statuses without recording history is disallowed.
/// </summary>
public class OrderStatusHistory
{
    public long Id { get; set; }

    public long OrderId { get; set; }

    /// <summary>
    /// Previous order status (null for initial order creation).
    /// </summary>
    public OrderStatus? PreviousStatus { get; set; }

    /// <summary>
    /// New order status applied.
    /// </summary>
    public OrderStatus NewStatus { get; set; }

    /// <summary>
    /// Optional human-readable reason or context for the transition.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// Optional user ID who initiated the transition (e.g. admin or customer).
    /// </summary>
    public string? ChangedByUserId { get; set; }

    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public Order Order { get; set; } = null!;
    public ApplicationUser? ChangedByUser { get; set; }
}
