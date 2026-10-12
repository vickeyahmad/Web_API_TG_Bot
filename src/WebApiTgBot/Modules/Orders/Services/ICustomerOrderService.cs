using WebApiTgBot.Common.Models;
using WebApiTgBot.Modules.Orders.Models.DTOs;

namespace WebApiTgBot.Modules.Orders.Services;

/// <summary>
/// Service contract for customer-facing order placement and tracking.
/// Guarantees atomic transaction execution, server-side price resolution,
/// durable idempotency, and strict customer ownership isolation.
/// </summary>
public interface ICustomerOrderService
{
    /// <summary>
    /// Creates a new order in PendingPayment status for an AI tool product.
    /// Computes prices server-side, captures immutable snapshots, and enforces durable idempotency.
    /// </summary>
    Task<CustomerOrderResult> CreateOrderAsync(
        string customerId,
        CreateCustomerOrderRequest request,
        string? idempotencyKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a paginated list of orders strictly belonging to the authenticated customer.
    /// </summary>
    Task<PagedResult<CustomerOrderSummaryDto>> GetCustomerOrdersAsync(
        string customerId,
        CustomerOrderQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves detailed information for a specific order belonging to the authenticated customer.
    /// Returns null if the order does not exist or belongs to another customer.
    /// </summary>
    Task<CustomerOrderDetailDto?> GetCustomerOrderDetailAsync(
        string customerId,
        string orderNumber,
        CancellationToken cancellationToken = default);
}
