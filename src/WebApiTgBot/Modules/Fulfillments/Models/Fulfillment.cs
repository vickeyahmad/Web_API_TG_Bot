using WebApiTgBot.Modules.Orders.Models;

namespace WebApiTgBot.Modules.Fulfillments.Models;

/// <summary>
/// Central fulfillment record tracking the delivery of a purchased order item.
/// Supports both manual fulfillment and future automated vendor API dispatch.
/// Completely decoupled from financial amounts and customer payment statuses.
/// </summary>
public class Fulfillment
{
    public long Id { get; set; }

    /// <summary>Foreign key linking to the parent Order.</summary>
    public long OrderId { get; set; }

    /// <summary>Foreign key linking to the specific purchased OrderItem.</summary>
    public long OrderItemId { get; set; }

    /// <summary>Current fulfillment status.</summary>
    public FulfillmentStatus Status { get; set; } = FulfillmentStatus.NotStarted;

    /// <summary>Mechanism used for fulfillment (Unassigned, Manual, VendorApi).</summary>
    public FulfillmentMethod FulfillmentMethod { get; set; } = FulfillmentMethod.Unassigned;

    /// <summary>Total quantity requested by customer (from OrderItem.Quantity, >= 1).</summary>
    public int QuantityRequested { get; set; }

    /// <summary>Total quantity fulfilled so far (0 &lt;= QuantityFulfilled &lt;= QuantityRequested).</summary>
    public int QuantityFulfilled { get; set; } = 0;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    // Navigation properties
    public Order Order { get; set; } = null!;
    public OrderItem OrderItem { get; set; } = null!;
    public ICollection<FulfillmentAttempt> Attempts { get; set; } = new List<FulfillmentAttempt>();
    public ICollection<FulfillmentStatusHistory> StatusHistory { get; set; } = new List<FulfillmentStatusHistory>();
}
