using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WebApiTgBot.Common.Models;
using WebApiTgBot.Infrastructure.Data;
using WebApiTgBot.Modules.Payments.Models;
using WebApiTgBot.Modules.Payments.Models.DTOs;

namespace WebApiTgBot.Modules.Payments.Services;

/// <summary>
/// Service implementation for administrator read-only querying of central payments.
/// Provides performant searching, comprehensive detail views, deterministic sorting, and safe DTO projections.
/// Operates in strict read-only mode with zero mutations to payments, orders, or wallets.
/// </summary>
public class AdminPaymentService : IAdminPaymentService
{
    private readonly AppDbContext _db;
    private readonly ILogger<AdminPaymentService> _logger;

    public AdminPaymentService(AppDbContext db, ILogger<AdminPaymentService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<PagedResult<AdminPaymentSummaryDto>> GetPaymentsAsync(AdminPaymentQuery query, CancellationToken ct = default)
    {
        var baseQuery = _db.Payments.AsNoTracking();

        // 1. Status filter
        if (query.Status.HasValue)
        {
            baseQuery = baseQuery.Where(p => p.Status == query.Status.Value);
        }

        // 2. Purpose filter
        if (query.Purpose.HasValue)
        {
            baseQuery = baseQuery.Where(p => p.Purpose == query.Purpose.Value);
        }

        // 3. Payment Method filter
        if (!string.IsNullOrWhiteSpace(query.PaymentMethod))
        {
            var methodUpper = query.PaymentMethod.Trim().ToUpperInvariant();
            baseQuery = baseQuery.Where(p => p.PaymentMethodCode.ToUpper() == methodUpper);
        }

        // 4. Multi-field search
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var cleanSearch = query.Search.Trim().ToLower();
            baseQuery = baseQuery.Where(p =>
                p.PaymentNumber.ToLower().Contains(cleanSearch) ||
                (p.ProviderTransactionId != null && p.ProviderTransactionId.ToLower().Contains(cleanSearch)) ||
                (p.CustomerReference != null && p.CustomerReference.ToLower().Contains(cleanSearch)) ||
                (p.Order != null && (
                    p.Order.CustomerId.ToLower().Contains(cleanSearch) ||
                    (p.Order.Customer != null && (
                        (p.Order.Customer.Email != null && p.Order.Customer.Email.ToLower().Contains(cleanSearch)) ||
                        (p.Order.Customer.FullName != null && p.Order.Customer.FullName.ToLower().Contains(cleanSearch))
                    ))
                )) ||
                (p.Wallet != null && (
                    p.Wallet.CustomerId.ToLower().Contains(cleanSearch) ||
                    (p.Wallet.Customer != null && (
                        (p.Wallet.Customer.Email != null && p.Wallet.Customer.Email.ToLower().Contains(cleanSearch)) ||
                        (p.Wallet.Customer.FullName != null && p.Wallet.Customer.FullName.ToLower().Contains(cleanSearch))
                    ))
                ))
            );
        }

        // 5. Total count for server-side pagination
        var totalCount = await baseQuery.CountAsync(ct);

        // 6. Deterministic ordering: CreatedAt descending, unique Id tie-breaker
        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize < 1 ? 20 : (query.PageSize > 100 ? 100 : query.PageSize);

        var payments = await baseQuery
            .OrderByDescending(p => p.CreatedAt)
            .ThenByDescending(p => p.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Include(p => p.Order)
                .ThenInclude(o => o!.Customer)
            .Include(p => p.Wallet)
                .ThenInclude(w => w!.Customer)
            .ToListAsync(ct);

        // 7. Project to safe admin summary DTOs
        var items = payments.Select(p =>
        {
            var isOrder = p.Purpose == PaymentPurpose.OrderPurchase;
            var customer = isOrder ? p.Order?.Customer : p.Wallet?.Customer;
            var customerId = isOrder ? (p.Order?.CustomerId ?? string.Empty) : (p.Wallet?.CustomerId ?? string.Empty);

            return new AdminPaymentSummaryDto
            {
                Id = p.Id,
                PaymentNumber = p.PaymentNumber,
                Purpose = p.Purpose.ToString(),
                CustomerId = customerId,
                CustomerFullName = customer?.FullName,
                CustomerEmail = customer?.Email,
                OrderNumber = p.Order?.OrderNumber,
                WalletId = p.WalletId,
                Amount = p.Amount,
                Currency = p.Currency,
                PaymentMethodCode = p.PaymentMethodCode,
                Status = p.Status.ToString(),
                CreatedAt = p.CreatedAt,
                UpdatedAt = p.UpdatedAt,
                PaidAt = p.PaidAt
            };
        }).ToList();

        return new PagedResult<AdminPaymentSummaryDto>(items, page, pageSize, totalCount);
    }

    /// <inheritdoc />
    public async Task<AdminPaymentDetailDto?> GetPaymentByNumberAsync(string paymentNumber, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(paymentNumber))
        {
            return null;
        }

        var cleanNumber = paymentNumber.Trim();

        var payment = await _db.Payments
            .AsNoTracking()
            .Include(p => p.Order)
                .ThenInclude(o => o!.Customer)
            .Include(p => p.Wallet)
                .ThenInclude(w => w!.Customer)
            .Include(p => p.VerifiedByUser)
            .Include(p => p.StatusHistory)
                .ThenInclude(h => h.ChangedByUser)
            .FirstOrDefaultAsync(p => p.PaymentNumber == cleanNumber, ct);

        if (payment == null)
        {
            return null;
        }

        var isOrder = payment.Purpose == PaymentPurpose.OrderPurchase;
        var customer = isOrder ? payment.Order?.Customer : payment.Wallet?.Customer;
        var customerId = isOrder ? (payment.Order?.CustomerId ?? string.Empty) : (payment.Wallet?.CustomerId ?? string.Empty);

        return new AdminPaymentDetailDto
        {
            Id = payment.Id,
            PaymentNumber = payment.PaymentNumber,
            Purpose = payment.Purpose.ToString(),
            CustomerId = customerId,
            CustomerFullName = customer?.FullName,
            CustomerEmail = customer?.Email,
            OrderId = payment.OrderId,
            OrderNumber = payment.Order?.OrderNumber,
            WalletId = payment.WalletId,
            Amount = payment.Amount,
            Currency = payment.Currency,
            PaymentMethodCode = payment.PaymentMethodCode,
            Status = payment.Status.ToString(),
            ProviderTransactionId = payment.ProviderTransactionId,
            CustomerReference = payment.CustomerReference,
            CustomerTransferDate = payment.CustomerTransferDate,
            CustomerSenderAccount = MinimizeAccountNumber(payment.CustomerSenderAccount),
            CustomerNotes = payment.CustomerNotes,
            CustomerProofSubmittedAt = payment.CustomerProofSubmittedAt,
            VerifiedByUserId = payment.VerifiedByUserId,
            VerifiedByUserName = payment.VerifiedByUser?.UserName,
            VerifiedReceivingAccount = MinimizeAccountNumber(payment.VerifiedReceivingAccount),
            AdminNotes = payment.AdminNotes,
            VerifiedAt = payment.VerifiedAt,
            CreatedAt = payment.CreatedAt,
            UpdatedAt = payment.UpdatedAt,
            PaidAt = payment.PaidAt,
            StatusHistory = payment.StatusHistory
                .OrderBy(h => h.ChangedAt)
                .Select(h => new PaymentStatusHistoryDto
                {
                    Id = h.Id,
                    PreviousStatus = h.PreviousStatus?.ToString(),
                    NewStatus = h.NewStatus.ToString(),
                    Reason = h.Reason,
                    ChangedByUserId = h.ChangedByUserId,
                    ChangedByUserName = h.ChangedByUser?.UserName,
                    ChangedAt = h.ChangedAt
                })
                .ToList()
        };
    }

    /// <summary>
    /// Safely masks/minimizes bank account or phone numbers to prevent plaintext leakage of sensitive identifiers.
    /// Preserves only the last 4 characters if longer than 4 characters.
    /// </summary>
    private static string? MinimizeAccountNumber(string? account)
    {
        if (string.IsNullOrWhiteSpace(account))
        {
            return null;
        }

        var trimmed = account.Trim();
        if (trimmed.Length <= 4)
        {
            return trimmed;
        }

        return $"***{trimmed[^4..]}";
    }
}
