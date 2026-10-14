namespace WebApiTgBot.Modules.Fulfillments.Models;

/// <summary>
/// Safety rules governing fulfillment attempts and supplier fallbacks.
/// Crucial safeguard: Automatic fallback to a secondary supplier is ONLY permitted
/// when the previous supplier returned a CONFIRMED failure.
/// Network timeouts or unknown outcomes CANNOT trigger automatic fallback,
/// preventing duplicate order purchases across suppliers.
/// </summary>
public static class FulfillmentAttemptRules
{
    /// <summary>
    /// Evaluates whether an automated fallback to another supplier is safe.
    /// Returns true ONLY for ConfirmedFailed.
    /// Returns false for Unknown, RequiresReconciliation, Submitted, NotStarted, Succeeded.
    /// </summary>
    public static bool CanFallbackToNextVendor(FulfillmentAttemptStatus status)
    {
        return status == FulfillmentAttemptStatus.ConfirmedFailed;
    }

    /// <summary>
    /// Indicates whether the attempt requires active investigation or reconciliation
    /// before any further action is taken.
    /// </summary>
    public static bool RequiresReconciliation(FulfillmentAttemptStatus status)
    {
        return status == FulfillmentAttemptStatus.Unknown || status == FulfillmentAttemptStatus.RequiresReconciliation;
    }

    /// <summary>
    /// Evaluates whether the attempt status is a terminal state for the attempt itself.
    /// </summary>
    public static bool IsAttemptTerminal(FulfillmentAttemptStatus status)
    {
        return status == FulfillmentAttemptStatus.Succeeded || status == FulfillmentAttemptStatus.ConfirmedFailed;
    }
}
