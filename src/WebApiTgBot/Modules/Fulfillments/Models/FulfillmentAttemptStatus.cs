namespace WebApiTgBot.Modules.Fulfillments.Models;

/// <summary>
/// Lifecycle statuses for an individual fulfillment attempt with a supplier or manual operator.
/// </summary>
public enum FulfillmentAttemptStatus
{
    /// <summary>Attempt prepared but not yet submitted to the supplier.</summary>
    NotStarted = 0,

    /// <summary>Submitted to the supplier; awaiting completion or confirmation.</summary>
    Submitted = 1,

    /// <summary>Supplier or manual operator confirmed successful fulfillment.</summary>
    Succeeded = 2,

    /// <summary>Supplier confirmed a definitive failure (e.g. out of stock, invalid parameters, rejected).</summary>
    ConfirmedFailed = 3,

    /// <summary>
    /// Ambiguous outcome: network timeout, gateway timeout, socket reset.
    /// It is UNKNOWN whether the supplier processed the order.
    /// Automatic fallback to another supplier is STRICTLY PROHIBITED in this state.
    /// </summary>
    Unknown = 4,

    /// <summary>
    /// Flagged for administrative or automated reconciliation before any subsequent action.
    /// </summary>
    RequiresReconciliation = 5
}
