using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using WebApiTgBot.Common.Models;
using WebApiTgBot.Modules.Fulfillments.Models;
using WebApiTgBot.Modules.Fulfillments.Models.DTOs;
using WebApiTgBot.Modules.Fulfillments.Services;
using WebApiTgBot.Modules.Identity.Models;
using WebApiTgBot.Modules.Orders.Models;

namespace WebApiTgBot.Modules.Fulfillments.Controllers;

/// <summary>
/// Administrator endpoints for managing and tracking order line item fulfillments.
/// Restricted strictly to the Administrator role and protected by CSRF token validation on mutative actions.
/// </summary>
[ApiController]
[Route("api/admin/orders")]
[Authorize(Roles = AppRoles.Admin)]
public class AdminFulfillmentsController : ControllerBase
{
    private readonly IFulfillmentService _fulfillmentService;

    public AdminFulfillmentsController(IFulfillmentService fulfillmentService)
    {
        _fulfillmentService = fulfillmentService;
    }

    /// <summary>
    /// Explicit administrator operation to initiate manual fulfillment for a specific line item on a Processing order.
    /// Creates a tracked manual fulfillment attempt (VendorProductId = null), records admin attribution and status history,
    /// and guards against duplicate delivery.
    /// </summary>
    [HttpPost("{orderNumber}/items/{orderItemId}/manual-fulfillment/start")]
    [EnableRateLimiting("AdminOrderLimiter")]
    [ProducesResponseType(typeof(ApiResponse<AdminFulfillmentDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<AdminFulfillmentDto>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<AdminFulfillmentDto>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<AdminFulfillmentDto>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<AdminFulfillmentDto>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<AdminFulfillmentDto>), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> StartManualFulfillment(
        [FromRoute] string orderNumber,
        [FromRoute] long orderItemId,
        CancellationToken cancellationToken)
    {
        var adminUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(adminUserId))
        {
            return Unauthorized(ApiResponse<AdminFulfillmentDto>.Fail("Admin session is unauthenticated."));
        }

        var result = await _fulfillmentService.StartManualFulfillmentAsync(
            orderNumber,
            orderItemId,
            adminUserId,
            cancellationToken);

        return result.Status switch
        {
            ManualFulfillmentOperationStatus.Success =>
                Ok(ApiResponse<AdminFulfillmentDto>.Ok(result.Data!, result.Message)),

            ManualFulfillmentOperationStatus.NotFound =>
                NotFound(ApiResponse<AdminFulfillmentDto>.Fail(result.Message)),

            ManualFulfillmentOperationStatus.Forbidden =>
                StatusCode(StatusCodes.Status403Forbidden, ApiResponse<AdminFulfillmentDto>.Fail(result.Message)),

            ManualFulfillmentOperationStatus.Conflict =>
                Conflict(ApiResponse<AdminFulfillmentDto>.Fail(result.Message)),

            _ =>
                BadRequest(ApiResponse<AdminFulfillmentDto>.Fail(result.Message))
        };
    }

    /// <summary>
    /// Explicit administrator operation to confirm manual fulfillment completion after delivery via an external process.
    /// Requires explicit confirmation (ConfirmDelivered = true) and non-sensitive delivery evidence references.
    /// Sets QuantityFulfilled, records audit history, marks the attempt Succeeded, and completes the parent Order if all items are fulfilled.
    /// </summary>
    [HttpPost("{orderNumber}/items/{orderItemId}/manual-fulfillment/complete")]
    [EnableRateLimiting("AdminOrderLimiter")]
    [ProducesResponseType(typeof(ApiResponse<AdminFulfillmentDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<AdminFulfillmentDto>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<AdminFulfillmentDto>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<AdminFulfillmentDto>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<AdminFulfillmentDto>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<AdminFulfillmentDto>), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CompleteManualFulfillment(
        [FromRoute] string orderNumber,
        [FromRoute] long orderItemId,
        [FromBody] AdminCompleteManualFulfillmentRequest request,
        CancellationToken cancellationToken)
    {
        var adminUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(adminUserId))
        {
            return Unauthorized(ApiResponse<AdminFulfillmentDto>.Fail("Admin session is unauthenticated."));
        }

        var result = await _fulfillmentService.CompleteManualFulfillmentAsync(
            orderNumber,
            orderItemId,
            adminUserId,
            request,
            cancellationToken);

        return result.Status switch
        {
            ManualFulfillmentOperationStatus.Success =>
                Ok(ApiResponse<AdminFulfillmentDto>.Ok(result.Data!, result.Message)),

            ManualFulfillmentOperationStatus.NotFound =>
                NotFound(ApiResponse<AdminFulfillmentDto>.Fail(result.Message)),

            ManualFulfillmentOperationStatus.Forbidden =>
                StatusCode(StatusCodes.Status403Forbidden, ApiResponse<AdminFulfillmentDto>.Fail(result.Message)),

            ManualFulfillmentOperationStatus.Conflict =>
                Conflict(ApiResponse<AdminFulfillmentDto>.Fail(result.Message)),

            _ =>
                BadRequest(ApiResponse<AdminFulfillmentDto>.Fail(result.Message))
        };
    }

    /// <summary>
    /// Retrieves all line item fulfillments, supplier/manual attempts, and status audit history for an order.
    /// Strictly excludes digital delivery secrets and raw supplier credentials.
    /// </summary>
    [HttpGet("{orderNumber}/fulfillments")]
    [ProducesResponseType(typeof(ApiResponse<AdminOrderFulfillmentsDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<AdminOrderFulfillmentsDto>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<AdminOrderFulfillmentsDto>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<AdminOrderFulfillmentsDto>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOrderFulfillments(
        [FromRoute] string orderNumber,
        CancellationToken cancellationToken)
    {
        var result = await _fulfillmentService.GetOrderFulfillmentsAsync(orderNumber, cancellationToken);
        if (result == null)
        {
            return NotFound(ApiResponse<AdminOrderFulfillmentsDto>.Fail($"Order '{orderNumber}' was not found."));
        }

        return Ok(ApiResponse<AdminOrderFulfillmentsDto>.Ok(result, "Order fulfillments retrieved successfully."));
    }

    /// <summary>
    /// Searches and retrieves a paginated overview of fulfillment work items across customer orders.
    /// Supports filtering by queue/fulfillment status, fulfillment method, parent order status, customer/order search, and server-side pagination.
    /// Safely projects uninitialized actionable line items without creating database records.
    /// </summary>
    [HttpGet("/api/admin/fulfillments")]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<AdminFulfillmentSummaryDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<AdminFulfillmentSummaryDto>>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<AdminFulfillmentSummaryDto>>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<AdminFulfillmentSummaryDto>>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetFulfillments(
        [FromQuery] AdminFulfillmentQuery query,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            var errors = string.Join("; ", ModelState.Values
                .SelectMany(v => v.Errors)
                .Select(e => e.ErrorMessage));
            return BadRequest(ApiResponse<PagedResult<AdminFulfillmentSummaryDto>>.Fail($"Validation failed: {errors}"));
        }

        if (query.Page < 1)
        {
            return BadRequest(ApiResponse<PagedResult<AdminFulfillmentSummaryDto>>.Fail("Page must be greater than or equal to 1."));
        }

        if (query.PageSize < 1 || query.PageSize > 100)
        {
            return BadRequest(ApiResponse<PagedResult<AdminFulfillmentSummaryDto>>.Fail("Page size must be between 1 and 100."));
        }

        if (!string.IsNullOrWhiteSpace(query.Search) && query.Search.Trim().Length > 100)
        {
            return BadRequest(ApiResponse<PagedResult<AdminFulfillmentSummaryDto>>.Fail("Search term cannot exceed 100 characters."));
        }

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            var statusStr = query.Status.Trim();
            var isUninitialized = statusStr.Equals("Uninitialized", StringComparison.OrdinalIgnoreCase);
            var isValidFulfillmentStatus = Enum.TryParse<FulfillmentStatus>(statusStr, true, out _);

            if (!isUninitialized && !isValidFulfillmentStatus)
            {
                return BadRequest(ApiResponse<PagedResult<AdminFulfillmentSummaryDto>>.Fail(
                    $"Invalid status '{query.Status}'. Supported values are: Uninitialized, NotStarted, Pending, InProgress, Completed, PartiallyCompleted, Failed, RequiresReview."));
            }
        }

        if (!string.IsNullOrWhiteSpace(query.FulfillmentMethod))
        {
            var methodStr = query.FulfillmentMethod.Trim();
            if (!Enum.TryParse<FulfillmentMethod>(methodStr, true, out _))
            {
                return BadRequest(ApiResponse<PagedResult<AdminFulfillmentSummaryDto>>.Fail(
                    $"Invalid fulfillmentMethod '{query.FulfillmentMethod}'. Supported values are: Unassigned, Manual, VendorApi."));
            }
        }

        if (!string.IsNullOrWhiteSpace(query.OrderStatus))
        {
            var orderStatusStr = query.OrderStatus.Trim();
            if (!Enum.TryParse<OrderStatus>(orderStatusStr, true, out _))
            {
                return BadRequest(ApiResponse<PagedResult<AdminFulfillmentSummaryDto>>.Fail(
                    $"Invalid orderStatus '{query.OrderStatus}'."));
            }
        }

        var result = await _fulfillmentService.GetFulfillmentsAsync(query, cancellationToken);
        return Ok(ApiResponse<PagedResult<AdminFulfillmentSummaryDto>>.Ok(result, "Fulfillments retrieved successfully."));
    }
}
