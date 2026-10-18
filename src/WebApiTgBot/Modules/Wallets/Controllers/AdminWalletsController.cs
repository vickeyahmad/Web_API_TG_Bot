using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using WebApiTgBot.Common.Models;
using WebApiTgBot.Modules.Identity.Models;
using WebApiTgBot.Modules.Wallets.Models;
using WebApiTgBot.Modules.Wallets.Models.DTOs;
using WebApiTgBot.Modules.Wallets.Services;

namespace WebApiTgBot.Modules.Wallets.Controllers;

/// <summary>
/// Administrator endpoints for inspecting customer wallets and immutable transaction ledgers.
/// Strictly restricted to authenticated Administrators and operates in read-only mode.
/// </summary>
[ApiController]
[Route("api/admin/wallets")]
[Authorize(Roles = AppRoles.Admin)]
public class AdminWalletsController : ControllerBase
{
    private readonly IAdminWalletService _walletService;

    public AdminWalletsController(IAdminWalletService walletService)
    {
        _walletService = walletService;
    }

    /// <summary>
    /// Searches and retrieves a paginated overview of customer wallets.
    /// Returns authoritative stored balances without recalculation, mutation, or wallet initialization.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<AdminWalletSummaryDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<AdminWalletSummaryDto>>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<AdminWalletSummaryDto>>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<AdminWalletSummaryDto>>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetWallets(
        [FromQuery] AdminWalletQuery query,
        CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            var errors = string.Join("; ", ModelState.Values
                .SelectMany(v => v.Errors)
                .Select(e => e.ErrorMessage));
            return BadRequest(ApiResponse<PagedResult<AdminWalletSummaryDto>>.Fail($"Validation failed: {errors}"));
        }

        if (query.Page < 1)
        {
            return BadRequest(ApiResponse<PagedResult<AdminWalletSummaryDto>>.Fail("Page must be greater than or equal to 1."));
        }

        if (query.PageSize < 1 || query.PageSize > 100)
        {
            return BadRequest(ApiResponse<PagedResult<AdminWalletSummaryDto>>.Fail("Page size must be between 1 and 100."));
        }

        if (!string.IsNullOrWhiteSpace(query.Search) && query.Search.Trim().Length > 100)
        {
            return BadRequest(ApiResponse<PagedResult<AdminWalletSummaryDto>>.Fail("Search term must not exceed 100 characters."));
        }

        if (!string.IsNullOrWhiteSpace(query.Currency) && query.Currency.Trim().Length > 10)
        {
            return BadRequest(ApiResponse<PagedResult<AdminWalletSummaryDto>>.Fail("Currency code must not exceed 10 characters."));
        }

        var result = await _walletService.GetWalletsAsync(query, ct);
        return Ok(ApiResponse<PagedResult<AdminWalletSummaryDto>>.Ok(result, "Customer wallets retrieved successfully."));
    }

    /// <summary>
    /// Retrieves safe, paginated transaction history from the immutable ledger for a specific customer wallet.
    /// Returns exact stored balances before and after each transaction.
    /// </summary>
    [HttpGet("{walletId:long}/transactions")]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<AdminWalletTransactionDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<AdminWalletTransactionDto>>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<AdminWalletTransactionDto>>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<AdminWalletTransactionDto>>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<AdminWalletTransactionDto>>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetWalletTransactions(
        [FromRoute] long walletId,
        [FromQuery] AdminWalletTransactionQuery query,
        CancellationToken ct)
    {
        if (walletId <= 0)
        {
            return BadRequest(ApiResponse<PagedResult<AdminWalletTransactionDto>>.Fail("Wallet ID must be a positive integer."));
        }

        if (!ModelState.IsValid)
        {
            var errors = string.Join("; ", ModelState.Values
                .SelectMany(v => v.Errors)
                .Select(e => e.ErrorMessage));
            return BadRequest(ApiResponse<PagedResult<AdminWalletTransactionDto>>.Fail($"Validation failed: {errors}"));
        }

        if (query.Page < 1)
        {
            return BadRequest(ApiResponse<PagedResult<AdminWalletTransactionDto>>.Fail("Page must be greater than or equal to 1."));
        }

        if (query.PageSize < 1 || query.PageSize > 100)
        {
            return BadRequest(ApiResponse<PagedResult<AdminWalletTransactionDto>>.Fail("Page size must be between 1 and 100."));
        }

        if (!string.IsNullOrWhiteSpace(query.SourceType) && !WalletTransactionSource.IsValid(query.SourceType))
        {
            return BadRequest(ApiResponse<PagedResult<AdminWalletTransactionDto>>.Fail($"Invalid source type '{query.SourceType}'."));
        }

        var result = await _walletService.GetWalletTransactionsAsync(walletId, query, ct);
        if (result == null)
        {
            return NotFound(ApiResponse<PagedResult<AdminWalletTransactionDto>>.Fail($"Wallet with ID {walletId} was not found."));
        }

        return Ok(ApiResponse<PagedResult<AdminWalletTransactionDto>>.Ok(result, "Wallet transaction history retrieved successfully."));
    }
}
