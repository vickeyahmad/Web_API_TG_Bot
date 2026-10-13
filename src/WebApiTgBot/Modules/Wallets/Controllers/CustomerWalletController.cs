using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using WebApiTgBot.Common.Models;
using WebApiTgBot.Modules.Identity.Models;
using WebApiTgBot.Modules.Wallets.Models.DTOs;
using WebApiTgBot.Modules.Wallets.Services;

namespace WebApiTgBot.Modules.Wallets.Controllers;

/// <summary>
/// Authenticated read-only customer wallet endpoints.
/// Strictly limited to balance inquiry and safe paginated transaction ledger inspection.
/// All balance mutations are internal and automated; direct customer balance editing is prohibited.
/// </summary>
[ApiController]
[Route("api/customer/wallet")]
[Authorize(AuthenticationSchemes = AuthSchemes.CustomerScheme, Roles = AppRoles.Customer)]
public class CustomerWalletController : ControllerBase
{
    private readonly IWalletService _walletService;

    public CustomerWalletController(IWalletService walletService)
    {
        _walletService = walletService;
    }

    /// <summary>
    /// Retrieves the authenticated customer's current PKR wallet balance.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<CustomerWalletDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetWallet(CancellationToken ct)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(customerId))
        {
            return Unauthorized(ApiResponse<CustomerWalletDto>.Fail("Customer session is unauthenticated."));
        }

        var wallet = await _walletService.GetOrCreateWalletAsync(customerId, "PKR", ct);

        var dto = new CustomerWalletDto
        {
            Balance = wallet.Balance,
            Currency = wallet.Currency,
            UpdatedAt = wallet.UpdatedAt
        };

        return Ok(ApiResponse<CustomerWalletDto>.Ok(dto, "Customer wallet retrieved successfully."));
    }

    /// <summary>
    /// Retrieves safe, paginated transaction history for the authenticated customer's PKR wallet.
    /// Excludes internal operator identities and system credentials.
    /// </summary>
    [HttpGet("transactions")]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<CustomerWalletTransactionDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetTransactions(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(customerId))
        {
            return Unauthorized(ApiResponse<PagedResult<CustomerWalletTransactionDto>>.Fail("Customer session is unauthenticated."));
        }

        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 100) pageSize = 100;

        var wallet = await _walletService.GetOrCreateWalletAsync(customerId, "PKR", ct);
        var transactions = await _walletService.GetTransactionHistoryAsync(wallet.Id, page, pageSize, ct);

        var items = transactions.Select(t => new CustomerWalletTransactionDto
        {
            Id = t.Id,
            TransactionType = t.TransactionType.ToString(),
            Amount = t.Amount,
            Currency = t.Currency,
            BalanceBefore = t.BalanceBefore,
            BalanceAfter = t.BalanceAfter,
            SourceType = t.SourceType,
            Description = t.Description,
            CreatedAt = t.CreatedAt
        }).ToList();

        var pagedResult = new PagedResult<CustomerWalletTransactionDto>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = items.Count
        };

        return Ok(ApiResponse<PagedResult<CustomerWalletTransactionDto>>.Ok(pagedResult, "Wallet transactions retrieved successfully."));
    }
}
