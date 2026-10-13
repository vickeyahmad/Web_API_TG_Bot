using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using WebApiTgBot.Common.Models;
using WebApiTgBot.Modules.Identity.Models;
using WebApiTgBot.Modules.Payments.Models.DTOs;
using WebApiTgBot.Modules.Payments.Services;

namespace WebApiTgBot.Modules.Payments.Controllers;

/// <summary>
/// Authenticated customer endpoints for manual PKR wallet top-up requests and transfer proof submissions.
/// Strictly bound to the Customer authentication scheme and customer role.
/// </summary>
[ApiController]
[Route("api/customer/wallet/topups")]
[Authorize(AuthenticationSchemes = AuthSchemes.CustomerScheme, Roles = AppRoles.Customer)]
public class CustomerWalletTopUpsController : ControllerBase
{
    private readonly ICustomerWalletTopUpService _topUpService;

    public CustomerWalletTopUpsController(ICustomerWalletTopUpService topUpService)
    {
        _topUpService = topUpService;
    }

    /// <summary>
    /// Initiates a manual PKR wallet top-up attempt in Pending status.
    /// Rate-limited and protected by durable idempotency via X-Idempotency-Key.
    /// </summary>
    [HttpPost]
    [EnableRateLimiting("CustomerWalletTopUpLimiter")]
    [ProducesResponseType(typeof(ApiResponse<CustomerWalletTopUpDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<CustomerWalletTopUpDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<CustomerWalletTopUpDto>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<CustomerWalletTopUpDto>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<CustomerWalletTopUpDto>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<CustomerWalletTopUpDto>), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateTopUp(
        [FromBody] CreateCustomerWalletTopUpRequest request,
        CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(customerId))
        {
            return Unauthorized(ApiResponse<CustomerWalletTopUpDto>.Fail("Customer session is unauthenticated."));
        }

        if (!ModelState.IsValid)
        {
            var errors = string.Join("; ", ModelState.Values
                .SelectMany(v => v.Errors)
                .Select(e => e.ErrorMessage));
            return BadRequest(ApiResponse<CustomerWalletTopUpDto>.Fail($"Validation failed: {errors}"));
        }

        var headerKey = Request.Headers["X-Idempotency-Key"].FirstOrDefault();

        var result = await _topUpService.CreateTopUpAsync(
            customerId,
            request,
            headerKey,
            cancellationToken);

        return result.Status switch
        {
            WalletTopUpResultStatus.Success =>
                result.IsExistingIdempotent
                    ? Ok(ApiResponse<CustomerWalletTopUpDto>.Ok(result.TopUp!, result.Message))
                    : CreatedAtAction(nameof(GetTopUpDetail), new { paymentNumber = result.TopUp!.PaymentNumber },
                        ApiResponse<CustomerWalletTopUpDto>.Ok(result.TopUp!, result.Message)),

            WalletTopUpResultStatus.FeatureDisabled =>
                StatusCode(StatusCodes.Status403Forbidden, ApiResponse<CustomerWalletTopUpDto>.Fail(result.Message)),

            WalletTopUpResultStatus.Conflict =>
                Conflict(ApiResponse<CustomerWalletTopUpDto>.Fail(result.Message)),

            WalletTopUpResultStatus.NotFound =>
                NotFound(ApiResponse<CustomerWalletTopUpDto>.Fail(result.Message)),

            _ =>
                BadRequest(ApiResponse<CustomerWalletTopUpDto>.Fail(result.Message))
        };
    }

    /// <summary>
    /// Submits customer transfer proof (bank TID / reference). Status remains Pending awaiting admin verification.
    /// Wallet balance is NOT credited at this stage.
    /// </summary>
    [HttpPost("{paymentNumber}/proof")]
    [EnableRateLimiting("CustomerWalletTopUpLimiter")]
    [ProducesResponseType(typeof(ApiResponse<CustomerWalletTopUpDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<CustomerWalletTopUpDto>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<CustomerWalletTopUpDto>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<CustomerWalletTopUpDto>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SubmitProof(
        [FromRoute] string paymentNumber,
        [FromBody] SubmitTransferProofRequest request,
        CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(customerId))
        {
            return Unauthorized(ApiResponse<CustomerWalletTopUpDto>.Fail("Customer session is unauthenticated."));
        }

        if (!ModelState.IsValid)
        {
            var errors = string.Join("; ", ModelState.Values
                .SelectMany(v => v.Errors)
                .Select(e => e.ErrorMessage));
            return BadRequest(ApiResponse<CustomerWalletTopUpDto>.Fail($"Validation failed: {errors}"));
        }

        var result = await _topUpService.SubmitProofAsync(
            customerId,
            paymentNumber,
            request,
            cancellationToken);

        return result.Status switch
        {
            WalletTopUpResultStatus.Success =>
                Ok(ApiResponse<CustomerWalletTopUpDto>.Ok(result.TopUp!, result.Message)),

            WalletTopUpResultStatus.NotFound =>
                NotFound(ApiResponse<CustomerWalletTopUpDto>.Fail(result.Message)),

            _ =>
                BadRequest(ApiResponse<CustomerWalletTopUpDto>.Fail(result.Message))
        };
    }

    /// <summary>
    /// Lists wallet top-ups belonging to the authenticated customer.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<CustomerWalletTopUpDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetTopUps(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(customerId))
        {
            return Unauthorized(ApiResponse<PagedResult<CustomerWalletTopUpDto>>.Fail("Customer session is unauthenticated."));
        }

        var result = await _topUpService.GetCustomerTopUpsAsync(customerId, page, pageSize, cancellationToken);
        return Ok(ApiResponse<PagedResult<CustomerWalletTopUpDto>>.Ok(result, "Customer wallet top-ups retrieved successfully."));
    }

    /// <summary>
    /// Tracks single top-up by payment number. Enforces customer data isolation.
    /// </summary>
    [HttpGet("{paymentNumber}")]
    [ProducesResponseType(typeof(ApiResponse<CustomerWalletTopUpDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<CustomerWalletTopUpDto>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<CustomerWalletTopUpDto>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTopUpDetail(
        [FromRoute] string paymentNumber,
        CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(customerId))
        {
            return Unauthorized(ApiResponse<CustomerWalletTopUpDto>.Fail("Customer session is unauthenticated."));
        }

        var topUp = await _topUpService.GetCustomerTopUpByNumberAsync(customerId, paymentNumber, cancellationToken);
        if (topUp == null)
        {
            return NotFound(ApiResponse<CustomerWalletTopUpDto>.Fail($"Top-up '{paymentNumber}' was not found."));
        }

        return Ok(ApiResponse<CustomerWalletTopUpDto>.Ok(topUp, "Top-up details retrieved successfully."));
    }
}
