using WebApiTgBot.Common.Models;
using WebApiTgBot.Modules.Orders.Models.DTOs;

namespace WebApiTgBot.Modules.Orders.Services;

/// <summary>
/// Service interface for administrator order management across all sales channels.
/// Provides searching, detailed order inspection, and controlled order lifecycle progression.
/// </summary>
public interface IAdminOrderService
{
    /// <summary>
    /// Searches and retrieves a paginated list of customer orders matching the specified filter criteria.
    /// Orders are sorted newest first by default.
    /// </summary>
    Task<PagedResult<AdminOrderSummaryDto>> GetOrdersAsync(AdminOrderQuery query, CancellationToken ct = default);

    /// <summary>
    /// Retrieves full order details including historical line item price snapshots, status audit history,
    /// and associated OrderPurchase payment attempts.
    /// </summary>
    Task<AdminOrderDetailDto?> GetOrderDetailAsync(string orderNumber, CancellationToken ct = default);

    /// <summary>
    /// Transitions a verified Paid order to Processing status in a single atomic database transaction.
    /// Strictly verifies that a valid, trusted Paid OrderPurchase payment exists with matching total and currency.
    /// Safe against concurrent duplicate executions and idempotent without duplicate history entries.
    /// </summary>
    Task<AdminOrderOperationResult> StartProcessingAsync(
        string orderNumber,
        string adminUserId,
        AdminStartOrderProcessingRequest? request,
        CancellationToken ct = default);
}
