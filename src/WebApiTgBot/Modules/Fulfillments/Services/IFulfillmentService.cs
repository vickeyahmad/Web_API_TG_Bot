using WebApiTgBot.Common.Models;
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

    /// <summary>
    /// Starts manual fulfillment for an eligible line item on a Processing order.
    /// Verifies trusted payment, creates a tracked manual attempt (VendorProductId = null),
    /// records administrator attribution and status history, and guards against duplicate delivery.
    /// </summary>
    Task<ManualFulfillmentOperationResult> StartManualFulfillmentAsync(string orderNumber, long orderItemId, string adminUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Confirms manual fulfillment completion after an administrator delivers the product via an authorized external process.
    /// Enforces explicit confirmation and non-sensitive delivery evidence, sets QuantityFulfilled,
    /// marks the attempt Succeeded and the fulfillment Completed, records audit history,
    /// and completes the parent Order if all line items are now fulfilled.
    /// </summary>
    Task<ManualFulfillmentOperationResult> CompleteManualFulfillmentAsync(string orderNumber, long orderItemId, string adminUserId, AdminCompleteManualFulfillmentRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all fulfillments, attempts, and audit history for an order for administrator inspection.
    /// </summary>
    Task<AdminOrderFulfillmentsDto?> GetOrderFulfillmentsAsync(string orderNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches and retrieves a paginated list of fulfillment work items across customer orders.
    /// Supports filtering by queue/fulfillment status, fulfillment method, parent order status, and customer/order search.
    /// Safely projects uninitialized actionable line items without creating database records.
    /// </summary>
    Task<PagedResult<AdminFulfillmentSummaryDto>> GetFulfillmentsAsync(AdminFulfillmentQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves current administrative fulfillment capabilities and feature status based on server-side configuration.
    /// Optionally checks product eligibility if a product SKU is specified.
    /// Strictly read-only; executes zero database queries.
    /// </summary>
    AdminFulfillmentCapabilitiesDto GetCapabilities(string? productSku = null);
}
