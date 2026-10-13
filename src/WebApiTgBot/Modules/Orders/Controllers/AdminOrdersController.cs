using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using WebApiTgBot.Common.Models;
using WebApiTgBot.Modules.Identity.Models;
using WebApiTgBot.Modules.Orders.Models.DTOs;
using WebApiTgBot.Modules.Orders.Services;

namespace WebApiTgBot.Modules.Orders.Controllers;

/// <summary>
/// Administrator endpoints for searching, reviewing, and progressing customer orders.
/// Strictly restricted to the Administrator role and protected by CSRF token validation.
/// </summary>
[ApiController]
[Route("api/admin/orders")]
[Authorize(Roles = AppRoles.Admin)]
public class AdminOrdersController : ControllerBase
{
    private readonly IAdminOrderService _adminOrderService;

    public AdminOrdersController(IAdminOrderService adminOrderService)
    {
        _adminOrderService = adminOrderService;
    }

    /// <summary>
    /// Searches and retrieves a paginated list of customer orders across all sales channels.
    /// Supports filtering by status, sales channel, customer, order number, SKU, and date range.
    /// Sorted newest orders first by default.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<AdminOrderSummaryDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<AdminOrderSummaryDto>>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<AdminOrderSummaryDto>>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetOrders(
        [FromQuery] AdminOrderQuery query,
        CancellationToken cancellationToken)
    {
        var result = await _adminOrderService.GetOrdersAsync(query, cancellationToken);
        return Ok(ApiResponse<PagedResult<AdminOrderSummaryDto>>.Ok(result, "Orders retrieved successfully."));
    }

    /// <summary>
    /// Retrieves comprehensive order details including customer summary, line item historical price snapshots,
    /// complete status audit trail, and associated OrderPurchase payment attempts.
    /// </summary>
    [HttpGet("{orderNumber}")]
    [ProducesResponseType(typeof(ApiResponse<AdminOrderDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<AdminOrderDetailDto>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<AdminOrderDetailDto>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<AdminOrderDetailDto>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOrderDetail(
        [FromRoute] string orderNumber,
        CancellationToken cancellationToken)
    {
        var order = await _adminOrderService.GetOrderDetailAsync(orderNumber, cancellationToken);
        if (order == null)
        {
            return NotFound(ApiResponse<AdminOrderDetailDto>.Fail($"Order '{orderNumber}' was not found."));
        }

        return Ok(ApiResponse<AdminOrderDetailDto>.Ok(order, "Order details retrieved successfully."));
    }

    /// <summary>
    /// Explicit administrator operation to transition a verified Paid order into Processing status.
    /// Strictly verifies that a matching Paid OrderPurchase payment exists before advancing status.
    /// Executes atomically with pessimistic row-locking to ensure concurrency safety.
    /// </summary>
    [HttpPost("{orderNumber}/start-processing")]
    [EnableRateLimiting("AdminOrderLimiter")]
    [ProducesResponseType(typeof(ApiResponse<AdminOrderDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<AdminOrderDetailDto>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<AdminOrderDetailDto>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<AdminOrderDetailDto>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<AdminOrderDetailDto>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<AdminOrderDetailDto>), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> StartProcessing(
        [FromRoute] string orderNumber,
        [FromBody] AdminStartOrderProcessingRequest? request,
        CancellationToken cancellationToken)
    {
        var adminUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(adminUserId))
        {
            return Unauthorized(ApiResponse<AdminOrderDetailDto>.Fail("Admin session is unauthenticated."));
        }

        var result = await _adminOrderService.StartProcessingAsync(
            orderNumber,
            adminUserId,
            request,
            cancellationToken);

        return result.Status switch
        {
            AdminOrderOperationStatus.Success =>
                Ok(ApiResponse<AdminOrderDetailDto>.Ok(result.Data!, result.Message)),

            AdminOrderOperationStatus.NotFound =>
                NotFound(ApiResponse<AdminOrderDetailDto>.Fail(result.Message)),

            AdminOrderOperationStatus.Forbidden =>
                StatusCode(StatusCodes.Status403Forbidden, ApiResponse<AdminOrderDetailDto>.Fail(result.Message)),

            AdminOrderOperationStatus.Conflict =>
                Conflict(ApiResponse<AdminOrderDetailDto>.Fail(result.Message)),

            _ =>
                BadRequest(ApiResponse<AdminOrderDetailDto>.Fail(result.Message))
        };
    }
}
