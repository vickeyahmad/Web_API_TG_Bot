using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using WebApiTgBot.Common.Models;
using WebApiTgBot.Modules.Identity.Models;
using WebApiTgBot.Modules.Payments.Models;
using WebApiTgBot.Modules.Payments.Models.DTOs;
using WebApiTgBot.Modules.Payments.Services;

namespace WebApiTgBot.Modules.Payments.Controllers;

/// <summary>
/// Administrator endpoints for reviewing, approving, and rejecting manual payment top-ups.
/// Strictly restricted to Administrator role and protected by CSRF validation.
/// </summary>
[ApiController]
[Route("api/admin/payments/manual")]
[Authorize(Roles = AppRoles.Admin)]
public class AdminManualPaymentsController : ControllerBase
{
    private readonly IAdminPaymentVerificationService _verificationService;

    public AdminManualPaymentsController(IAdminPaymentVerificationService verificationService)
    {
        _verificationService = verificationService;
    }

    /// <summary>
    /// Lists manual payment top-ups with optional status filter and pagination.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<AdminManualPaymentDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetManualPayments(
        [FromQuery] PaymentStatus? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await _verificationService.GetManualPaymentsAsync(status, page, pageSize, cancellationToken);
        return Ok(ApiResponse<PagedResult<AdminManualPaymentDto>>.Ok(result, "Manual payments retrieved successfully."));
    }

    /// <summary>
    /// Retrieves comprehensive details and audit history of a manual payment by payment number.
    /// </summary>
    [HttpGet("{paymentNumber}")]
    [ProducesResponseType(typeof(ApiResponse<AdminManualPaymentDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<AdminManualPaymentDto>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetManualPaymentDetail(
        [FromRoute] string paymentNumber,
        CancellationToken cancellationToken)
    {
        var payment = await _verificationService.GetManualPaymentByNumberAsync(paymentNumber, cancellationToken);
        if (payment == null)
        {
            return NotFound(ApiResponse<AdminManualPaymentDto>.Fail($"Manual payment '{paymentNumber}' was not found."));
        }

        return Ok(ApiResponse<AdminManualPaymentDto>.Ok(payment, "Manual payment details retrieved successfully."));
    }

    /// <summary>
    /// Confirms receipt of customer funds, marks payment Paid, and atomically credits the customer's PKR wallet.
    /// </summary>
    [HttpPost("{paymentNumber}/approve")]
    [ProducesResponseType(typeof(ApiResponse<ApproveManualPaymentResultDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<ApproveManualPaymentResultDto>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<ApproveManualPaymentResultDto>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<ApproveManualPaymentResultDto>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<ApproveManualPaymentResultDto>), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ApprovePayment(
        [FromRoute] string paymentNumber,
        [FromBody] ApproveManualPaymentRequest request,
        CancellationToken cancellationToken)
    {
        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(adminId))
        {
            return Unauthorized(ApiResponse<ApproveManualPaymentResultDto>.Fail("Admin session is unauthenticated."));
        }

        if (!ModelState.IsValid)
        {
            var errors = string.Join("; ", ModelState.Values
                .SelectMany(v => v.Errors)
                .Select(e => e.ErrorMessage));
            return BadRequest(ApiResponse<ApproveManualPaymentResultDto>.Fail($"Validation failed: {errors}"));
        }

        var result = await _verificationService.ApproveManualTopUpAsync(
            adminId,
            paymentNumber,
            request,
            cancellationToken);

        return result.Status switch
        {
            AdminVerificationResultStatus.Success =>
                Ok(ApiResponse<ApproveManualPaymentResultDto>.Ok(result.ApprovalResult!, result.Message)),

            AdminVerificationResultStatus.Forbidden =>
                StatusCode(StatusCodes.Status403Forbidden, ApiResponse<ApproveManualPaymentResultDto>.Fail(result.Message)),

            AdminVerificationResultStatus.NotFound =>
                NotFound(ApiResponse<ApproveManualPaymentResultDto>.Fail(result.Message)),

            AdminVerificationResultStatus.Conflict =>
                Conflict(ApiResponse<ApproveManualPaymentResultDto>.Fail(result.Message)),

            _ =>
                BadRequest(ApiResponse<ApproveManualPaymentResultDto>.Fail(result.Message))
        };
    }

    /// <summary>
    /// Rejects a manual payment attempt and marks it Failed. Customer wallet is untouched.
    /// </summary>
    [HttpPost("{paymentNumber}/reject")]
    [ProducesResponseType(typeof(ApiResponse<AdminManualPaymentDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<AdminManualPaymentDto>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<AdminManualPaymentDto>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<AdminManualPaymentDto>), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RejectPayment(
        [FromRoute] string paymentNumber,
        [FromBody] RejectManualPaymentRequest request,
        CancellationToken cancellationToken)
    {
        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(adminId))
        {
            return Unauthorized(ApiResponse<AdminManualPaymentDto>.Fail("Admin session is unauthenticated."));
        }

        if (!ModelState.IsValid)
        {
            var errors = string.Join("; ", ModelState.Values
                .SelectMany(v => v.Errors)
                .Select(e => e.ErrorMessage));
            return BadRequest(ApiResponse<AdminManualPaymentDto>.Fail($"Validation failed: {errors}"));
        }

        var result = await _verificationService.RejectManualTopUpAsync(
            adminId,
            paymentNumber,
            request,
            cancellationToken);

        return result.Status switch
        {
            AdminVerificationResultStatus.Success =>
                Ok(ApiResponse<AdminManualPaymentDto>.Ok(result.Payment!, result.Message)),

            AdminVerificationResultStatus.NotFound =>
                NotFound(ApiResponse<AdminManualPaymentDto>.Fail(result.Message)),

            AdminVerificationResultStatus.Conflict =>
                Conflict(ApiResponse<AdminManualPaymentDto>.Fail(result.Message)),

            _ =>
                BadRequest(ApiResponse<AdminManualPaymentDto>.Fail(result.Message))
        };
    }
}
