using WebApiTgBot.Modules.Catalog.Models;
using WebApiTgBot.Modules.Identity.Models;

namespace WebApiTgBot.Modules.Fulfillments.Models;

/// <summary>
/// Records an individual fulfillment attempt with a supplier or manual operator.
/// Preserves complete historical audit trail of supplier selections, attempts, and outcomes.
/// Never stores secrets, customer passwords, or supplier API credentials.
/// </summary>
public class FulfillmentAttempt
{
    public long Id { get; set; }

    /// <summary>Foreign key linking to the parent Fulfillment record.</summary>
    public long FulfillmentId { get; set; }

    /// <summary>Sequential attempt number for this fulfillment (1, 2, 3...).</summary>
    public int AttemptNumber { get; set; }

    /// <summary>Durable, unique idempotency/reference key for this attempt.</summary>
    public string AttemptKey { get; set; } = string.Empty;

    /// <summary>
    /// Foreign key to the selected supplier mapping (VendorProduct).
    /// Nullable for manual fulfillment by internal staff.
    /// </summary>
    public int? VendorProductId { get; set; }

    /// <summary>Current status of this attempt.</summary>
    public FulfillmentAttemptStatus Status { get; set; } = FulfillmentAttemptStatus.NotStarted;

    /// <summary>
    /// External order reference returned by the supplier's API.
    /// Strictly null until a verified, trusted supplier response is received.
    /// </summary>
    public string? ExternalVendorOrderId { get; set; }

    /// <summary>Categorization of failure if attempt failed.</summary>
    public FulfillmentFailureCategory? FailureCategory { get; set; }

    /// <summary>
    /// Sanitized, safe summary of any error or note.
    /// NEVER stores raw exceptions, supplier API keys, customer credentials or tokens.
    /// </summary>
    public string? SafeErrorSummary { get; set; }

    /// <summary>
    /// ApplicationUser ID of the administrator who initiated this attempt.
    /// </summary>
    public string? InitiatedByUserId { get; set; }

    /// <summary>
    /// ApplicationUser ID of the administrator who confirmed successful manual delivery.
    /// </summary>
    public string? ConfirmedByUserId { get; set; }

    /// <summary>
    /// Safe external delivery reference or support ticket identifier.
    /// Strictly excludes credentials, license keys, or vouchers.
    /// </summary>
    public string? DeliveryReference { get; set; }

    /// <summary>
    /// Non-sensitive delivery channel identifier (e.g. EmailSupport, WhatsAppDirect).
    /// </summary>
    public string? DeliveryChannel { get; set; }

    /// <summary>
    /// Optional short, sanitized operational note.
    /// Strictly excludes digital delivery secrets or sensitive customer credentials.
    /// </summary>
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SubmittedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    // Navigation properties
    public Fulfillment Fulfillment { get; set; } = null!;
    public VendorProduct? VendorProduct { get; set; }
    public ApplicationUser? InitiatedByUser { get; set; }
    public ApplicationUser? ConfirmedByUser { get; set; }
}
