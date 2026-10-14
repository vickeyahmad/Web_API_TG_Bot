using WebApiTgBot.Modules.Identity.Models;

namespace WebApiTgBot.Modules.Fulfillments.Models;

/// <summary>
/// Permanent audit log for fulfillment lifecycle status transitions.
/// Preserves complete chronological history of who changed status, when, and safe evidence/reasons.
/// Cascading deletion is strictly prohibited to guarantee non-destructive audit retention.
/// </summary>
public class FulfillmentStatusHistory
{
    public long Id { get; set; }

    /// <summary>Foreign key linking to the parent Fulfillment record.</summary>
    public long FulfillmentId { get; set; }

    /// <summary>Previous fulfillment status (null for initial creation).</summary>
    public FulfillmentStatus? PreviousStatus { get; set; }

    /// <summary>New fulfillment status applied.</summary>
    public FulfillmentStatus NewStatus { get; set; }

    /// <summary>
    /// Safe operational reason, evidence reference, or delivery channel note.
    /// Strictly excludes passwords, license keys, vouchers, or supplier credentials.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// ApplicationUser ID of administrator who triggered the status change.
    /// </summary>
    public string? ChangedByUserId { get; set; }

    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public Fulfillment Fulfillment { get; set; } = null!;
    public ApplicationUser? ChangedByUser { get; set; }
}
