namespace WebApiTgBot.Modules.Orders.Models;

/// <summary>
/// Strongly typed lifecycle statuses for orders in the centralized digital reseller platform.
/// Kept separate from payment gateway transaction statuses and vendor fulfillment statuses.
/// </summary>
public enum OrderStatus
{
    /// <summary>Order created; awaiting customer payment verification.</summary>
    PendingPayment,

    /// <summary>Payment successfully verified.</summary>
    Paid,

    /// <summary>Order is being prepared for fulfillment.</summary>
    Processing,

    /// <summary>Fulfillment request dispatched to external supplier/vendor.</summary>
    SentToVendor,

    /// <summary>Vendor has acknowledged and is actively processing the order.</summary>
    VendorProcessing,

    /// <summary>Order has been completely fulfilled and delivered to customer.</summary>
    Completed,

    /// <summary>Order was partially fulfilled (e.g. partial quantity or partial subscription delivered).</summary>
    PartiallyCompleted,

    /// <summary>Fulfillment failed (e.g. vendor out of stock or rejected).</summary>
    Failed,

    /// <summary>Order was cancelled before fulfillment.</summary>
    Cancelled,

    /// <summary>A refund has been requested/initiated and is awaiting processing.</summary>
    RefundPending,

    /// <summary>Order amount has been refunded to customer.</summary>
    Refunded
}
