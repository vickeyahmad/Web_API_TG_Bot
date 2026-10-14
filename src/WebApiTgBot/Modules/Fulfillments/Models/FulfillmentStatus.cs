namespace WebApiTgBot.Modules.Fulfillments.Models;

/// <summary>
/// Strongly typed lifecycle statuses for order line item fulfillment.
/// Kept distinct from overall customer OrderStatus and PaymentStatus.
/// </summary>
public enum FulfillmentStatus
{
    /// <summary>Fulfillment record initialized; awaiting processing or assignment.</summary>
    NotStarted = 0,

    /// <summary>Queued and waiting for vendor API dispatch or manual assignment.</summary>
    Pending = 1,

    /// <summary>Actively being processed by a supplier or manual fulfillment staff.</summary>
    InProgress = 2,

    /// <summary>Fulfillment completely finished; full requested quantity delivered.</summary>
    Completed = 3,

    /// <summary>Partial quantity or service delivered (e.g. partial SMM count or portion of subscription).</summary>
    PartiallyCompleted = 4,

    /// <summary>Fulfillment definitively failed (e.g. out of stock, vendor rejection).</summary>
    Failed = 5,

    /// <summary>Ambiguous outcome (e.g. network timeout) requiring administrator review before retrying or falling back.</summary>
    RequiresReview = 6
}
