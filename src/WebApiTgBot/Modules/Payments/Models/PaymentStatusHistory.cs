using WebApiTgBot.Modules.Identity.Models;

namespace WebApiTgBot.Modules.Payments.Models;

/// <summary>
/// Permanent audit log for all payment status changes.
/// Preserved permanently for financial reconciliation, dispute resolution, and regulatory audit.
/// </summary>
public class PaymentStatusHistory
{
    public long Id { get; set; }

    public long PaymentId { get; set; }

    public PaymentStatus? PreviousStatus { get; set; }

    public PaymentStatus NewStatus { get; set; }

    /// <summary>
    /// Audit note, reason, or event description explaining the status change.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// ApplicationUser ID of administrator or system user who triggered the change (null for automated webhook/system).
    /// </summary>
    public string? ChangedByUserId { get; set; }

    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public Payment Payment { get; set; } = null!;
    public ApplicationUser? ChangedByUser { get; set; }
}
