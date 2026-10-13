namespace WebApiTgBot.Modules.Payments.Models;

/// <summary>
/// Enforces business rules for payment status lifecycle transitions.
/// Guarantees that payments cannot become Paid without trusted verification,
/// terminal states are respected, and historical integrity is maintained.
/// </summary>
public static class PaymentStatusTransitions
{
    private static readonly Dictionary<PaymentStatus, HashSet<PaymentStatus>> AllowedTransitions = new()
    {
        [PaymentStatus.Pending] = new()
        {
            PaymentStatus.Paid,
            PaymentStatus.Failed,
            PaymentStatus.Expired
        },
        [PaymentStatus.Paid] = new()
        {
            PaymentStatus.Refunded,
            PaymentStatus.PartiallyRefunded
        },
        [PaymentStatus.PartiallyRefunded] = new()
        {
            PaymentStatus.Refunded // Remainder refunded
        },
        [PaymentStatus.Failed] = new(), // Terminal
        [PaymentStatus.Expired] = new(), // Terminal
        [PaymentStatus.Refunded] = new() // Terminal
    };

    /// <summary>
    /// Checks whether transitioning from current status to target status is permitted.
    /// </summary>
    public static bool CanTransition(PaymentStatus from, PaymentStatus to)
    {
        if (from == to)
        {
            return false;
        }

        return AllowedTransitions.TryGetValue(from, out var allowed) && allowed.Contains(to);
    }

    /// <summary>
    /// Returns true if the status is a terminal state that cannot transition to any other status.
    /// </summary>
    public static bool IsTerminal(PaymentStatus status) =>
        status is PaymentStatus.Failed or PaymentStatus.Expired or PaymentStatus.Refunded;
}
