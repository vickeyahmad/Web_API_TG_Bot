namespace WebApiTgBot.Modules.Fulfillments.Models;

/// <summary>
/// State machine enforcing valid lifecycle transitions for line item fulfillment.
/// Prevents skipping steps (e.g. NotStarted directly to Completed) and locks terminal states.
/// </summary>
public static class FulfillmentStatusTransitions
{
    private static readonly Dictionary<FulfillmentStatus, HashSet<FulfillmentStatus>> AllowedTransitions = new()
    {
        [FulfillmentStatus.NotStarted] = new() { FulfillmentStatus.Pending, FulfillmentStatus.InProgress, FulfillmentStatus.Failed, FulfillmentStatus.RequiresReview },
        [FulfillmentStatus.Pending] = new() { FulfillmentStatus.InProgress, FulfillmentStatus.Failed, FulfillmentStatus.RequiresReview },
        [FulfillmentStatus.InProgress] = new() { FulfillmentStatus.Completed, FulfillmentStatus.PartiallyCompleted, FulfillmentStatus.Failed, FulfillmentStatus.RequiresReview },
        [FulfillmentStatus.PartiallyCompleted] = new() { FulfillmentStatus.InProgress, FulfillmentStatus.Completed, FulfillmentStatus.Failed, FulfillmentStatus.RequiresReview },
        [FulfillmentStatus.RequiresReview] = new() { FulfillmentStatus.InProgress, FulfillmentStatus.Completed, FulfillmentStatus.PartiallyCompleted, FulfillmentStatus.Failed },
        [FulfillmentStatus.Completed] = new(), // Terminal
        [FulfillmentStatus.Failed] = new()     // Terminal
    };

    /// <summary>
    /// Checks whether transitioning from current to next status is permitted by the state machine.
    /// </summary>
    public static bool CanTransition(FulfillmentStatus current, FulfillmentStatus next)
    {
        if (current == next) return true;
        return AllowedTransitions.TryGetValue(current, out var allowed) && allowed.Contains(next);
    }

    /// <summary>
    /// Returns true if the status is a terminal state (Completed or Failed).
    /// </summary>
    public static bool IsTerminal(FulfillmentStatus status)
    {
        return status == FulfillmentStatus.Completed || status == FulfillmentStatus.Failed;
    }
}
