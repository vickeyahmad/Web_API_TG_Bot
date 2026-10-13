using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using WebApiTgBot.Common.Models;
using WebApiTgBot.Modules.Identity.Models;
using WebApiTgBot.Modules.Orders.Models.DTOs;
using WebApiTgBot.Modules.Orders.Services;
using WebApiTgBot.Modules.Payments.Models.DTOs;
using WebApiTgBot.Modules.Payments.Services;

namespace WebApiTgBot.Modules.Orders.Controllers;

/// <summary>
/// Authenticated customer order management and tracking endpoints.
/// Supports order placement with durable idempotency, order history listing,
/// order detail tracking, and wallet order payments.
/// Customer identity and sales channel are strictly resolved server-side.
/// </summary>
[ApiController]
[Route("api/customer/orders")]
[Authorize(AuthenticationSchemes = AuthSchemes.CustomerScheme, Roles = AppRoles.Customer)]
public class CustomerOrdersController : ControllerBase
{
    private readonly ICustomerOrderService _orderService;
    private readonly IWalletOrderPaymentService _walletPaymentService;

    public CustomerOrdersController(
        ICustomerOrderService orderService,
        IWalletOrderPaymentService walletPaymentService)
    {
        _orderService = orderService;
        _walletPaymentService = walletPaymentService;
    }

    /// <summary>
    /// Places a new order in PendingPayment status for an AI tool product.
    /// Requires active Customer session and valid Antiforgery/CSRF token.
    /// Rate limited and protected by durable idempotency via X-Idempotency-Key.
    /// </summary>
    [HttpPost]
    [EnableRateLimiting("CustomerOrderLimiter")]
    [ProducesResponseType(typeof(ApiResponse<CustomerOrderDetailDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<CustomerOrderDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<CustomerOrderDetailDto>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<CustomerOrderDetailDto>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<CustomerOrderDetailDto>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<CustomerOrderDetailDto>), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateOrder(
        [FromBody] CreateCustomerOrderRequest request,
        CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(customerId))
        {
            return Unauthorized(ApiResponse<CustomerOrderDetailDto>.Fail("Customer session is unauthenticated."));
        }

        if (!ModelState.IsValid)
        {
            var errors = string.Join("; ", ModelState.Values
                .SelectMany(v => v.Errors)
                .Select(e => e.ErrorMessage));
            return BadRequest(ApiResponse<CustomerOrderDetailDto>.Fail($"Validation failed: {errors}"));
        }

        // Accept idempotency key from HTTP header or request body
        var headerKey = Request.Headers["X-Idempotency-Key"].FirstOrDefault();

        var result = await _orderService.CreateOrderAsync(
            customerId,
            request,
            headerKey,
            cancellationToken);

        return result.Status switch
        {
            CustomerOrderResultStatus.Success =>
                result.IsExistingIdempotent
                    ? Ok(ApiResponse<CustomerOrderDetailDto>.Ok(result.Order!, result.Message))
                    : CreatedAtAction(nameof(GetOrderDetail), new { orderNumber = result.Order!.OrderNumber },
                        ApiResponse<CustomerOrderDetailDto>.Ok(result.Order!, result.Message)),

            CustomerOrderResultStatus.NotFound =>
                NotFound(ApiResponse<CustomerOrderDetailDto>.Fail(result.Message)),

            CustomerOrderResultStatus.Conflict =>
                Conflict(ApiResponse<CustomerOrderDetailDto>.Fail(result.Message)),

            CustomerOrderResultStatus.Forbidden =>
                StatusCode(StatusCodes.Status403Forbidden, ApiResponse<CustomerOrderDetailDto>.Fail(result.Message)),

            _ =>
                BadRequest(ApiResponse<CustomerOrderDetailDto>.Fail(result.Message))
        };
    }

    /// <summary>
    /// Lists orders belonging to the authenticated customer with pagination and optional status filter.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<CustomerOrderSummaryDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetOrders(
        [FromQuery] CustomerOrderQuery query,
        CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(customerId))
        {
            return Unauthorized(ApiResponse<PagedResult<CustomerOrderSummaryDto>>.Fail("Customer session is unauthenticated."));
        }

        var orders = await _orderService.GetCustomerOrdersAsync(customerId, query, cancellationToken);
        return Ok(ApiResponse<PagedResult<CustomerOrderSummaryDto>>.Ok(orders, "Customer orders retrieved successfully."));
    }

    /// <summary>
    /// Retrieves detailed order and status history for a specific order belonging to the authenticated customer.
    /// Returns 404 if the order does not exist or belongs to another customer.
    /// </summary>
    [HttpGet("{orderNumber}")]
    [ProducesResponseType(typeof(ApiResponse<CustomerOrderDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<CustomerOrderDetailDto>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<CustomerOrderDetailDto>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOrderDetail(
        string orderNumber,
        CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(customerId))
        {
            return Unauthorized(ApiResponse<CustomerOrderDetailDto>.Fail("Customer session is unauthenticated."));
        }

        var order = await _orderService.GetCustomerOrderDetailAsync(customerId, orderNumber, cancellationToken);
        if (order == null)
        {
            return NotFound(ApiResponse<CustomerOrderDetailDto>.Fail($"Order '{orderNumber}' was not found."));
        }

        return Ok(ApiResponse<CustomerOrderDetailDto>.Ok(order, "Order details retrieved successfully."));
    }

    /// <summary>
    /// Pays an existing PendingPayment order using the customer's available PKR wallet balance.
    /// Requires active Customer session and valid Antiforgery/CSRF token.
    /// Rate limited and protected by durable idempotency via X-Idempotency-Key.
    /// </summary>
    [HttpPost("{orderNumber}/pay-with-wallet")]
    [EnableRateLimiting("CustomerOrderLimiter")]
    [ProducesResponseType(typeof(ApiResponse<CustomerOrderPaymentDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<CustomerOrderPaymentDto>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<CustomerOrderPaymentDto>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<CustomerOrderPaymentDto>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<CustomerOrderPaymentDto>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<CustomerOrderPaymentDto>), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> PayWithWallet(
        string orderNumber,
        CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(customerId))
        {
            return Unauthorized(ApiResponse<CustomerOrderPaymentDto>.Fail("Customer session is unauthenticated."));
        }

        var headerKey = Request.Headers["X-Idempotency-Key"].FirstOrDefault();

        var result = await _walletPaymentService.PayOrderWithWalletAsync(
            customerId,
            orderNumber,
            headerKey,
            cancellationToken);

        return result.Status switch
        {
            WalletOrderPaymentStatus.Success =>
                Ok(ApiResponse<CustomerOrderPaymentDto>.Ok(result.Data!, result.Message)),

            WalletOrderPaymentStatus.NotFound =>
                NotFound(ApiResponse<CustomerOrderPaymentDto>.Fail(result.Message)),

            WalletOrderPaymentStatus.Forbidden =>
                StatusCode(StatusCodes.Status403Forbidden, ApiResponse<CustomerOrderPaymentDto>.Fail(result.Message)),

            WalletOrderPaymentStatus.Unauthorized =>
                Unauthorized(ApiResponse<CustomerOrderPaymentDto>.Fail(result.Message)),

            WalletOrderPaymentStatus.Conflict =>
                Conflict(ApiResponse<CustomerOrderPaymentDto>.Fail(result.Message)),

            WalletOrderPaymentStatus.InsufficientFunds =>
                BadRequest(ApiResponse<CustomerOrderPaymentDto>.Fail(result.Message)),

            _ =>
                BadRequest(ApiResponse<CustomerOrderPaymentDto>.Fail(result.Message))
        };
    }
}
