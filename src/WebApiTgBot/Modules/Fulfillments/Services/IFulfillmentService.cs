using WebApiTgBot.Modules.Fulfillments.Models;
using WebApiTgBot.Modules.Fulfillments.Models.DTOs;

namespace WebApiTgBot.Modules.Fulfillments.Services;

/// <summary>
/// Service contract for internal fulfillment management, eligibility validation,
/// and safe supplier attempt tracking.
/// </summary>
public interface IFulfillmentService
{
    /// <summary>
    /// Safely initializes or retrieves the fulfillment tracking record for an order line item.
    /// Enforces that the order is paid or processing with verified funds, and prevents duplicate initialization.
    /// </summary>
    Task<FulfillmentInitializationResult> InitializeFulfillmentForOrderItemAsync(long orderId, long orderItemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the fulfillment record associated with a specific order item.
    /// </summary>
    Task<Fulfillment?> GetFulfillmentByOrderItemIdAsync(long orderItemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all fulfillment records for an order.
    /// </summary>
    Task<IReadOnlyList<Fulfillment>> GetFulfillmentsByOrderIdAsync(long orderId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Prepares a new fulfillment attempt with an assigned vendor product mapping or manual operator.
    /// Enforces that previous attempts with unconfirmed outcomes cannot trigger automatic fallback.
    /// </summary>
    Task<FulfillmentAttemptResult> CreateAttemptAsync(long fulfillmentId, int? vendorProductId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the outcome of an attempt (e.g. Succeeded, ConfirmedFailed, Unknown, RequiresReconciliation).
    /// </summary>
    Task<FulfillmentAttemptResult> RecordAttemptOutcomeAsync(long attemptId, FulfillmentAttemptStatus outcomeStatus, FulfillmentFailureCategory? failureCategory = null, string? safeErrorSummary = null, string? externalVendorOrderId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates whether an automated fallback to another supplier is safe based on the attempt status.
    /// </summary>
    bool CanFallbackAfterAttempt(FulfillmentAttemptStatus status);
}
