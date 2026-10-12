namespace WebApiTgBot.Modules.Orders.Models;

/// <summary>
/// Domain rules defining valid, controlled order status transitions.
/// Enforces business state integrity: prevents illegal status jumps and protects terminal states.
/// </summary>
public static class OrderStatusTransitions
{
    private static readonly Dictionary<OrderStatus, HashSet<OrderStatus>> AllowedTransitions = new()
    {
        [OrderStatus.PendingPayment] = new()
        {
            OrderStatus.Paid,
            OrderStatus.Cancelled,
            OrderStatus.Failed
        },
        [OrderStatus.Paid] = new()
        {
            OrderStatus.Processing,
            OrderStatus.RefundPending,
            OrderStatus.Cancelled
        },
        [OrderStatus.Processing] = new()
        {
            OrderStatus.SentToVendor,
            OrderStatus.Completed,
            OrderStatus.PartiallyCompleted,
            OrderStatus.Failed,
            OrderStatus.RefundPending
        },
        [OrderStatus.SentToVendor] = new()
        {
            OrderStatus.VendorProcessing,
            OrderStatus.Completed,
            OrderStatus.PartiallyCompleted,
            OrderStatus.Failed
        },
        [OrderStatus.VendorProcessing] = new()
        {
            OrderStatus.Completed,
            OrderStatus.PartiallyCompleted,
            OrderStatus.Failed
        },
        [OrderStatus.PartiallyCompleted] = new()
        {
            OrderStatus.Completed,
            OrderStatus.RefundPending
        },
        [OrderStatus.Failed] = new()
        {
            OrderStatus.Processing,
            OrderStatus.RefundPending
        },
        [OrderStatus.RefundPending] = new()
        {
            OrderStatus.Refunded,
            OrderStatus.Completed
        },
        [OrderStatus.Cancelled] = new(), // Terminal state
        [OrderStatus.Refunded] = new(),  // Terminal state
        [OrderStatus.Completed] = new()  // May transition to RefundPending if dispute arises
        {
            OrderStatus.RefundPending
        }
    };

    /// <summary>
    /// Checks whether transitioning from currentStatus to targetStatus is legally permitted.
    /// </summary>
    public static bool IsValidTransition(OrderStatus currentStatus, OrderStatus targetStatus)
    {
        if (currentStatus == targetStatus)
        {
            return true;
        }

        return AllowedTransitions.TryGetValue(currentStatus, out var allowed) && allowed.Contains(targetStatus);
    }
}
