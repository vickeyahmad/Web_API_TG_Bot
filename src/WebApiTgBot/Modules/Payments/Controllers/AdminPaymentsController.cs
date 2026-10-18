using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using WebApiTgBot.Common.Models;
using WebApiTgBot.Modules.Identity.Models;
using WebApiTgBot.Modules.Payments.Models;
using WebApiTgBot.Modules.Payments.Models.DTOs;
using WebApiTgBot.Modules.Payments.Services;

namespace WebApiTgBot.Modules.Payments.Controllers;

/// <summary>
/// Administrator endpoints for inspecting general customer payments across all purposes (OrderPurchase and WalletTopUp).
/// Strictly restricted to authenticated Administrators and operates in read-only mode.
/// </summary>
[ApiController]
[Route("api/admin/payments")]
[Authorize(Roles = AppRoles.Admin)]
public class AdminPaymentsController : ControllerBase
{
    private readonly IAdminPaymentService _paymentService;

    public AdminPaymentsController(IAdminPaymentService paymentService)
    {
        _paymentService = paymentService;
    }

    /// <summary>
    /// Searches and retrieves a paginated overview of all customer payments across the platform.
    /// Supports filtering by status, purpose, payment method, customer search, and server-side pagination.
    /// Sorted newest first by creation date.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<AdminPaymentSummaryDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<AdminPaymentSummaryDto>>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<AdminPaymentSummaryDto>>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<AdminPaymentSummaryDto>>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetPayments(
        [FromQuery] AdminPaymentQuery query,
        CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            var errors = string.Join("; ", ModelState.Values
                .SelectMany(v => v.Errors)
                .Select(e => e.ErrorMessage));
            return BadRequest(ApiResponse<PagedResult<AdminPaymentSummaryDto>>.Fail($"Validation failed: {errors}"));
        }

        if (query.Page < 1)
        {
            return BadRequest(ApiResponse<PagedResult<AdminPaymentSummaryDto>>.Fail("Page must be greater than or equal to 1."));
        }

        if (query.PageSize < 1 || query.PageSize > 100)
        {
            return BadRequest(ApiResponse<PagedResult<AdminPaymentSummaryDto>>.Fail("Page size must be between 1 and 100."));
        }

        if (!string.IsNullOrWhiteSpace(query.Search) && query.Search.Trim().Length > 100)
        {
            return BadRequest(ApiResponse<PagedResult<AdminPaymentSummaryDto>>.Fail("Search term must not exceed 100 characters."));
        }

        if (!string.IsNullOrWhiteSpace(query.PaymentMethod) && !PaymentMethodCodes.IsValid(query.PaymentMethod))
        {
            return BadRequest(ApiResponse<PagedResult<AdminPaymentSummaryDto>>.Fail($"Invalid payment method '{query.PaymentMethod}'."));
        }

        var result = await _paymentService.GetPaymentsAsync(query, ct);
        return Ok(ApiResponse<PagedResult<AdminPaymentSummaryDto>>.Ok(result, "Payments retrieved successfully."));
    }

    /// <summary>
    /// Retrieves safe, comprehensive administrative details of an individual payment by its payment number.
    /// Supports both OrderPurchase and WalletTopUp payments and returns chronological status audit history.
    /// </summary>
    [HttpGet("{paymentNumber:regex(^(?!manual$).*$)}")]
    [ProducesResponseType(typeof(ApiResponse<AdminPaymentDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<AdminPaymentDetailDto>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<AdminPaymentDetailDto>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<AdminPaymentDetailDto>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<AdminPaymentDetailDto>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPaymentDetail(
        [FromRoute] string paymentNumber,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(paymentNumber) || paymentNumber.Trim().Length > 50)
        {
            return BadRequest(ApiResponse<AdminPaymentDetailDto>.Fail("Payment number must be provided and not exceed 50 characters."));
        }

        var payment = await _paymentService.GetPaymentByNumberAsync(paymentNumber, ct);
        if (payment == null)
        {
            return NotFound(ApiResponse<AdminPaymentDetailDto>.Fail($"Payment '{paymentNumber}' was not found."));
        }

        return Ok(ApiResponse<AdminPaymentDetailDto>.Ok(payment, "Payment details retrieved successfully."));
    }
}
