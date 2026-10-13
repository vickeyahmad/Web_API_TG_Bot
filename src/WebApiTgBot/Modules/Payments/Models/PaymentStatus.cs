namespace WebApiTgBot.Modules.Payments.Models;

/// <summary>
/// Strongly typed lifecycle statuses for customer payment attempts.
/// Decoupled from OrderStatus (which tracks overall order fulfillment) and vendor settlement.
/// </summary>
public enum PaymentStatus
{
    /// <summary>Payment attempt created and awaiting customer action or external verification.</summary>
    Pending,

    /// <summary>Payment funds successfully received and verified through trusted channel.</summary>
    Paid,

    /// <summary>Payment attempt failed (e.g. insufficient funds, provider rejection, cancelled by user).</summary>
    Failed,

    /// <summary>Payment session window expired without receiving funds.</summary>
    Expired,

    /// <summary>Payment has been completely refunded back to customer.</summary>
    Refunded,

    /// <summary>A portion of the payment has been refunded back to customer.</summary>
    PartiallyRefunded
}
