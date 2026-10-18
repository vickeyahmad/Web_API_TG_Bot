using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WebApiTgBot.Common.Models;
using WebApiTgBot.Infrastructure.Data;
using WebApiTgBot.Modules.Wallets.Models.DTOs;

namespace WebApiTgBot.Modules.Wallets.Services;

/// <summary>
/// Service implementation for administrator read-only querying of customer wallets and immutable transaction ledgers.
/// Operates strictly without modifying balances, creating wallets, or appending ledger entries.
/// </summary>
public class AdminWalletService : IAdminWalletService
{
    private readonly AppDbContext _db;
    private readonly ILogger<AdminWalletService> _logger;

    public AdminWalletService(AppDbContext db, ILogger<AdminWalletService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<PagedResult<AdminWalletSummaryDto>> GetWalletsAsync(AdminWalletQuery query, CancellationToken ct = default)
    {
        var baseQuery = _db.Wallets.AsNoTracking();

        // 1. Currency filter
        if (!string.IsNullOrWhiteSpace(query.Currency))
        {
            var currencyUpper = query.Currency.Trim().ToUpperInvariant();
            baseQuery = baseQuery.Where(w => w.Currency.ToUpper() == currencyUpper);
        }

        // 2. Search filter (CustomerId, Email, FullName)
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var cleanSearch = query.Search.Trim().ToLower();
            baseQuery = baseQuery.Where(w =>
                w.CustomerId.ToLower().Contains(cleanSearch) ||
                (w.Customer != null && (
                    (w.Customer.Email != null && w.Customer.Email.ToLower().Contains(cleanSearch)) ||
                    (w.Customer.FullName != null && w.Customer.FullName.ToLower().Contains(cleanSearch))
                ))
            );
        }

        // 3. Total count for pagination
        var totalCount = await baseQuery.CountAsync(ct);

        // 4. Deterministic ordering: CreatedAt descending with unique Id tie-breaker
        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize < 1 ? 20 : (query.PageSize > 100 ? 100 : query.PageSize);

        var wallets = await baseQuery
            .OrderByDescending(w => w.CreatedAt)
            .ThenByDescending(w => w.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Include(w => w.Customer)
            .ToListAsync(ct);

        // 5. Project to safe admin summary DTOs
        var items = wallets.Select(w => new AdminWalletSummaryDto
        {
            WalletId = w.Id,
            CustomerId = w.CustomerId,
            CustomerFullName = w.Customer?.FullName,
            CustomerEmail = w.Customer?.Email,
            CurrentBalance = w.Balance,
            Currency = w.Currency,
            CreatedAt = w.CreatedAt,
            UpdatedAt = w.UpdatedAt
        }).ToList();

        return new PagedResult<AdminWalletSummaryDto>(items, page, pageSize, totalCount);
    }

    /// <inheritdoc />
    public async Task<PagedResult<AdminWalletTransactionDto>?> GetWalletTransactionsAsync(
        long walletId,
        AdminWalletTransactionQuery query,
        CancellationToken ct = default)
    {
        // 1. Verify target wallet exists; pure read-only check without creating or initializing
        var walletExists = await _db.Wallets.AsNoTracking().AnyAsync(w => w.Id == walletId, ct);
        if (!walletExists)
        {
            return null;
        }

        var baseQuery = _db.WalletTransactions.AsNoTracking().Where(t => t.WalletId == walletId);

        // 2. Transaction direction filter (Credit, Debit)
        if (query.TransactionType.HasValue)
        {
            baseQuery = baseQuery.Where(t => t.TransactionType == query.TransactionType.Value);
        }

        // 3. Source category filter
        if (!string.IsNullOrWhiteSpace(query.SourceType))
        {
            var cleanSource = query.SourceType.Trim();
            baseQuery = baseQuery.Where(t => t.SourceType.ToLower() == cleanSource.ToLower());
        }

        // 4. Total count for pagination
        var totalCount = await baseQuery.CountAsync(ct);

        // 5. Deterministic ordering: CreatedAt descending with unique Id tie-breaker
        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize < 1 ? 20 : (query.PageSize > 100 ? 100 : query.PageSize);

        var transactions = await baseQuery
            .OrderByDescending(t => t.CreatedAt)
            .ThenByDescending(t => t.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        // 6. Project to safe admin transaction DTOs
        var items = transactions.Select(t => new AdminWalletTransactionDto
        {
            TransactionId = t.Id,
            WalletId = t.WalletId,
            TransactionType = t.TransactionType.ToString(),
            Amount = t.Amount,
            Currency = t.Currency,
            BalanceBefore = t.BalanceBefore,
            BalanceAfter = t.BalanceAfter,
            SourceType = t.SourceType,
            SourceReferenceId = t.SourceReferenceId,
            Description = t.Description,
            CreatedAt = t.CreatedAt
        }).ToList();

        return new PagedResult<AdminWalletTransactionDto>(items, page, pageSize, totalCount);
    }
}
