using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using System.Data;
using WebApiTgBot.Common.Models;
using WebApiTgBot.Infrastructure.Data;
using WebApiTgBot.Modules.Orders.Models;
using WebApiTgBot.Modules.Orders.Models.DTOs;
using WebApiTgBot.Modules.Payments.Models;

namespace WebApiTgBot.Modules.Orders.Services;

/// <summary>
/// Service implementation for administrator order management across all sales channels.
/// Provides performant searching, comprehensive detail views, and atomic state machine transitions.
/// </summary>
public class AdminOrderService : IAdminOrderService
{
    private readonly AppDbContext _db;
    private readonly ILogger<AdminOrderService> _logger;

    public AdminOrderService(AppDbContext db, ILogger<AdminOrderService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<PagedResult<AdminOrderSummaryDto>> GetOrdersAsync(AdminOrderQuery query, CancellationToken ct = default)
    {
        var baseQuery = _db.Orders.AsNoTracking();

        // 1. Status filter
        if (query.Status.HasValue)
        {
            baseQuery = baseQuery.Where(o => o.Status == query.Status.Value);
        }

        // 2. SalesChannel filter
        if (query.SalesChannel.HasValue)
        {
            baseQuery = baseQuery.Where(o => o.SalesChannel == query.SalesChannel.Value);
        }

        // 3. OrderNumber search
        if (!string.IsNullOrWhiteSpace(query.OrderNumber))
        {
            var cleanNum = query.OrderNumber.Trim().ToLower();
            baseQuery = baseQuery.Where(o => o.OrderNumber.ToLower().Contains(cleanNum));
        }

        // 4. Product SKU search (matches historical snapshot)
        if (!string.IsNullOrWhiteSpace(query.ProductSku))
        {
            var cleanSku = query.ProductSku.Trim().ToLower();
            baseQuery = baseQuery.Where(o => o.Items.Any(i => i.ProductSkuSnapshot.ToLower().Contains(cleanSku)));
        }

        // 5. Customer search (Email, FullName, or CustomerId)
        if (!string.IsNullOrWhiteSpace(query.CustomerQuery))
        {
            var cleanCust = query.CustomerQuery.Trim().ToLower();
            baseQuery = baseQuery.Where(o =>
                o.CustomerId.ToLower().Contains(cleanCust) ||
                (o.Customer != null && (
                    (o.Customer.Email != null && o.Customer.Email.ToLower().Contains(cleanCust)) ||
                    (o.Customer.FullName != null && o.Customer.FullName.ToLower().Contains(cleanCust))
                )));
        }

        // 6. Date range filter
        if (query.FromDate.HasValue)
        {
            baseQuery = baseQuery.Where(o => o.CreatedAt >= query.FromDate.Value);
        }

        if (query.ToDate.HasValue)
        {
            baseQuery = baseQuery.Where(o => o.CreatedAt <= query.ToDate.Value);
        }

        // 7. Total count for pagination
        var totalCount = await baseQuery.CountAsync(ct);

        // 8. Sorting (Newest orders first by default) and pagination
        var orders = await baseQuery
            .OrderByDescending(o => o.CreatedAt)
            .ThenByDescending(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Include(o => o.Customer)
            .Include(o => o.Items)
            .Include(o => o.Payments.Where(p => p.Purpose == PaymentPurpose.OrderPurchase))
            .ToListAsync(ct);

        // 9. Map to administrator summary DTOs
        var items = orders.Select(o =>
        {
            // Payment summary: prefer confirmed Paid payment; fallback to latest OrderPurchase payment attempt
            var paidPayment = o.Payments.FirstOrDefault(p => p.Status == PaymentStatus.Paid);
            var latestPayment = paidPayment ?? o.Payments.OrderByDescending(p => p.CreatedAt).FirstOrDefault();

            AdminOrderPaymentSummaryDto? paymentSummary = null;
            if (latestPayment != null)
            {
                paymentSummary = new AdminOrderPaymentSummaryDto
                {
                    PaymentNumber = latestPayment.PaymentNumber,
                    PaymentMethod = latestPayment.PaymentMethodCode,
                    PaymentStatus = latestPayment.Status.ToString(),
                    AmountPaid = latestPayment.Amount,
                    Currency = latestPayment.Currency,
                    PaidAt = latestPayment.PaidAt
                };
            }

            // Build human-readable product summary
            string productSummary;
            var firstItem = o.Items.FirstOrDefault();
            if (firstItem == null)
            {
                productSummary = "No items";
            }
            else if (o.Items.Count == 1)
            {
                productSummary = $"{firstItem.ProductNameSnapshot} ({firstItem.ProductSkuSnapshot}) x{firstItem.Quantity}";
            }
            else
            {
                productSummary = $"{firstItem.ProductNameSnapshot} ({firstItem.ProductSkuSnapshot}) x{firstItem.Quantity} + {o.Items.Count - 1} more";
            }

            return new AdminOrderSummaryDto
            {
                OrderNumber = o.OrderNumber,
                OrderId = o.Id,
                CustomerId = o.CustomerId,
                CustomerEmail = o.Customer?.Email,
                CustomerName = o.Customer?.FullName,
                ProductSummary = productSummary,
                ItemCount = o.Items.Sum(i => i.Quantity),
                TotalAmount = o.TotalAmount,
                Currency = o.Currency,
                Status = o.Status.ToString(),
                SalesChannel = o.SalesChannel.ToString(),
                CreatedAt = o.CreatedAt,
                UpdatedAt = o.UpdatedAt,
                PaymentSummary = paymentSummary
            };
        }).ToList();

        return new PagedResult<AdminOrderSummaryDto>(items, totalCount, query.Page, query.PageSize);
    }

    public async Task<AdminOrderDetailDto?> GetOrderDetailAsync(string orderNumber, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(orderNumber))
        {
            return null;
        }

        var order = await _db.Orders.AsNoTracking()
            .Include(o => o.Customer)
            .Include(o => o.Items)
            .Include(o => o.StatusHistory)
            .Include(o => o.Payments.Where(p => p.Purpose == PaymentPurpose.OrderPurchase))
            .FirstOrDefaultAsync(o => o.OrderNumber == orderNumber.Trim(), ct);

        if (order == null)
        {
            return null;
        }

        return MapToDetailDto(order);
    }

    public async Task<AdminOrderOperationResult> StartProcessingAsync(
        string orderNumber,
        string adminUserId,
        AdminStartOrderProcessingRequest? request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(orderNumber))
        {
            return AdminOrderOperationResult.BadRequest("OrderNumber is required.");
        }

        if (string.IsNullOrWhiteSpace(adminUserId))
        {
            return AdminOrderOperationResult.Forbidden("Admin session is unauthenticated.");
        }

        IDbContextTransaction? tx = null;
        if (_db.Database.IsRelational())
        {
            tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        }

        try
        {
            // 1. Acquire pessimistic row-level lock on the Order entity in PostgreSQL
            Order? order;
            if (_db.Database.IsRelational())
            {
                order = await _db.Orders
                    .FromSqlInterpolated($"SELECT * FROM \"Orders\" WHERE \"OrderNumber\" = {orderNumber.Trim()} FOR UPDATE")
                    .FirstOrDefaultAsync(ct);
            }
            else
            {
                order = await _db.Orders
                    .FirstOrDefaultAsync(o => o.OrderNumber == orderNumber.Trim(), ct);
            }

            if (order == null)
            {
                if (tx != null) await tx.RollbackAsync(ct);
                return AdminOrderOperationResult.NotFound($"Order '{orderNumber}' was not found.");
            }

            // 2. Verify customer existence
            var customer = await _db.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == order.CustomerId, ct);

            if (customer == null)
            {
                if (tx != null) await tx.RollbackAsync(ct);
                return AdminOrderOperationResult.BadRequest($"Order '{orderNumber}' is associated with an invalid or deleted customer identity.");
            }

            // 3. Status checks and concurrency conflict detection
            if (order.Status == OrderStatus.Processing)
            {
                if (tx != null) await tx.RollbackAsync(ct);
                return AdminOrderOperationResult.Conflict($"Order '{orderNumber}' is already in Processing status.");
            }

            if (order.Status == OrderStatus.PendingPayment)
            {
                if (tx != null) await tx.RollbackAsync(ct);
                return AdminOrderOperationResult.BadRequest($"Cannot start processing unpaid order. Order '{orderNumber}' is in PendingPayment status.");
            }

            if (order.Status != OrderStatus.Paid)
            {
                if (tx != null) await tx.RollbackAsync(ct);
                return AdminOrderOperationResult.BadRequest($"Cannot start processing order in '{order.Status}' status. Only Paid orders can transition to Processing.");
            }

            // 4. Validate domain state machine rules
            if (!OrderStatusTransitions.IsValidTransition(order.Status, OrderStatus.Processing))
            {
                if (tx != null) await tx.RollbackAsync(ct);
                return AdminOrderOperationResult.BadRequest($"Transition from '{order.Status}' to 'Processing' is not permitted by order lifecycle rules.");
            }

            // 5. Verify trusted Paid OrderPurchase payment record
            var paidPayment = await _db.Payments
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.OrderId == order.Id &&
                                          p.Purpose == PaymentPurpose.OrderPurchase &&
                                          p.Status == PaymentStatus.Paid, ct);

            if (paidPayment == null)
            {
                if (tx != null) await tx.RollbackAsync(ct);
                _logger.LogWarning("Admin order processing rejected: Order {OrderNumber} marked Paid has no matching Paid payment record", orderNumber);
                return AdminOrderOperationResult.BadRequest($"Cannot start processing order '{orderNumber}'. No verified Paid payment record was found.");
            }

            if (paidPayment.Amount != order.TotalAmount)
            {
                if (tx != null) await tx.RollbackAsync(ct);
                _logger.LogWarning("Admin order processing rejected: Order {OrderNumber} payment amount mismatch ({PaidAmount} != {OrderTotal})",
                    orderNumber, paidPayment.Amount, order.TotalAmount);
                return AdminOrderOperationResult.BadRequest($"Cannot start processing order '{orderNumber}'. Verified payment amount ({paidPayment.Amount:F4} {paidPayment.Currency}) does not match order total ({order.TotalAmount:F4} {order.Currency}).");
            }

            if (!string.Equals(paidPayment.Currency, order.Currency, StringComparison.OrdinalIgnoreCase))
            {
                if (tx != null) await tx.RollbackAsync(ct);
                _logger.LogWarning("Admin order processing rejected: Order {OrderNumber} payment currency mismatch ({PaidCurr} != {OrderCurr})",
                    orderNumber, paidPayment.Currency, order.Currency);
                return AdminOrderOperationResult.BadRequest($"Cannot start processing order '{orderNumber}'. Verified payment currency ({paidPayment.Currency}) does not match order currency ({order.Currency}).");
            }

            // 6. Transition Order to Processing status
            var now = DateTime.UtcNow;
            order.Status = OrderStatus.Processing;
            order.UpdatedAt = now;

            var reason = !string.IsNullOrWhiteSpace(request?.Reason)
                ? request.Reason.Trim()
                : "Administrator started order processing.";

            // 7. Record immutable OrderStatusHistory entry
            var historyEntry = new OrderStatusHistory
            {
                OrderId = order.Id,
                PreviousStatus = OrderStatus.Paid,
                NewStatus = OrderStatus.Processing,
                Reason = reason,
                ChangedByUserId = adminUserId,
                ChangedAt = now
            };
            _db.OrderStatusHistories.Add(historyEntry);

            // 8. Commit single atomic PostgreSQL transaction
            await _db.SaveChangesAsync(ct);

            if (tx != null)
            {
                await tx.CommitAsync(ct);
            }

            _logger.LogInformation("Order {OrderNumber} transitioned to Processing by Admin {AdminUserId}", orderNumber, adminUserId);

            // 9. Load complete detail DTO for response
            var updatedDetail = await GetOrderDetailAsync(orderNumber, ct);
            return AdminOrderOperationResult.Success(updatedDetail!, $"Order '{orderNumber}' transitioned to Processing successfully.");
        }
        catch (DbUpdateException ex)
        {
            if (tx != null) await tx.RollbackAsync(ct);

            var pgEx = ex.InnerException as PostgresException;
            if (pgEx != null && (pgEx.SqlState == "40P01" || pgEx.SqlState == "55P03"))
            {
                _logger.LogWarning(ex, "Concurrency conflict/deadlock while starting processing for order {OrderNumber}", orderNumber);
                return AdminOrderOperationResult.Conflict("The transaction could not be completed due to concurrent conflict. Please retry your request.");
            }

            _logger.LogError(ex, "DbUpdateException while starting processing for order {OrderNumber}", orderNumber);
            throw;
        }
        catch (Exception ex)
        {
            if (tx != null) await tx.RollbackAsync(ct);

            var pgEx = ex as PostgresException ?? ex.InnerException as PostgresException;
            if (pgEx != null && (pgEx.SqlState == "40P01" || pgEx.SqlState == "55P03"))
            {
                _logger.LogWarning(ex, "Concurrency conflict/deadlock while starting processing for order {OrderNumber}", orderNumber);
                return AdminOrderOperationResult.Conflict("The transaction could not be completed due to concurrent conflict. Please retry your request.");
            }

            _logger.LogError(ex, "Unexpected error while starting processing for order {OrderNumber}", orderNumber);
            throw;
        }
        finally
        {
            if (tx != null)
            {
                await tx.DisposeAsync();
            }
        }
    }

    private static AdminOrderDetailDto MapToDetailDto(Order order)
    {
        return new AdminOrderDetailDto
        {
            OrderNumber = order.OrderNumber,
            OrderId = order.Id,
            CustomerId = order.CustomerId,
            CustomerEmail = order.Customer?.Email,
            CustomerName = order.Customer?.FullName,
            CustomerPhoneNumber = order.Customer?.PhoneNumber,
            SalesChannel = order.SalesChannel.ToString(),
            Status = order.Status.ToString(),
            Subtotal = order.Subtotal,
            TotalAmount = order.TotalAmount,
            Currency = order.Currency,
            CustomerNotes = order.CustomerNotes,
            CreatedAt = order.CreatedAt,
            UpdatedAt = order.UpdatedAt,
            Items = order.Items.Select(i => new AdminOrderItemDto
            {
                ProductSku = i.ProductSkuSnapshot,
                ProductName = i.ProductNameSnapshot,
                Quantity = i.Quantity,
                UnitPrice = i.UnitPriceSnapshot,
                TotalPrice = i.TotalPrice,
                Currency = i.Currency
            }).ToList(),
            StatusHistory = order.StatusHistory
                .OrderBy(h => h.ChangedAt)
                .Select(h => new AdminOrderStatusHistoryDto
                {
                    Id = h.Id,
                    PreviousStatus = h.PreviousStatus?.ToString(),
                    NewStatus = h.NewStatus.ToString(),
                    Reason = h.Reason,
                    ChangedByUserId = h.ChangedByUserId,
                    ChangedAt = h.ChangedAt
                }).ToList(),
            Payments = order.Payments
                .Where(p => p.Purpose == PaymentPurpose.OrderPurchase)
                .OrderByDescending(p => p.CreatedAt)
                .Select(p => new AdminOrderPaymentDto
                {
                    PaymentNumber = p.PaymentNumber,
                    PaymentMethod = p.PaymentMethodCode,
                    Amount = p.Amount,
                    Currency = p.Currency,
                    Status = p.Status.ToString(),
                    CreatedAt = p.CreatedAt,
                    PaidAt = p.PaidAt,
                    ExpiredAt = p.ExpiredAt,
                    ProviderTransactionId = p.ProviderTransactionId
                }).ToList()
        };
    }
}
