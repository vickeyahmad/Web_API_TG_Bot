using WebApiTgBot.Modules.Catalog.Models;

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

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SubmittedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    // Navigation properties
    public Fulfillment Fulfillment { get; set; } = null!;
    public VendorProduct? VendorProduct { get; set; }
}
