using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using System.Data;
using WebApiTgBot.Infrastructure.Data;
using WebApiTgBot.Modules.Orders.Models;
using WebApiTgBot.Modules.Payments.Models;
using WebApiTgBot.Modules.Payments.Models.DTOs;
using WebApiTgBot.Modules.Payments.Options;
using WebApiTgBot.Modules.Wallets.Models;
using WebApiTgBot.Modules.Wallets.Services;

namespace WebApiTgBot.Modules.Payments.Services;

/// <summary>
/// Implements atomic order payment processing using available customer PKR wallet balance.
/// Enforces single-transaction execution across Orders, Payments, and Wallets with
/// pessimistic row-level locking (SELECT ... FOR UPDATE) in PostgreSQL to eliminate concurrency race conditions.
/// </summary>
public class WalletOrderPaymentService : IWalletOrderPaymentService
{
    private readonly AppDbContext _db;
    private readonly IWalletService _walletService;
    private readonly IPaymentNumberGenerator _paymentNumberGenerator;
    private readonly WalletPurchaseOptions _options;
    private readonly ILogger<WalletOrderPaymentService> _logger;

    public WalletOrderPaymentService(
        AppDbContext db,
        IWalletService walletService,
        IPaymentNumberGenerator paymentNumberGenerator,
        IOptions<WalletPurchaseOptions> options,
        ILogger<WalletOrderPaymentService> logger)
    {
        _db = db;
        _walletService = walletService;
        _paymentNumberGenerator = paymentNumberGenerator;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<WalletOrderPaymentResult> PayOrderWithWalletAsync(
        string customerId,
        string orderNumber,
        string? idempotencyKey,
        CancellationToken ct = default)
    {
        // 1. Feature Gate Check — Prohibits wallet order payment when feature is disabled
        if (!_options.Enabled)
        {
            return WalletOrderPaymentResult.Forbidden("Wallet order payments are currently disabled by server configuration.");
        }

        // 2. Input validation
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return WalletOrderPaymentResult.Unauthorized("Customer session is unauthenticated.");
        }

        if (string.IsNullOrWhiteSpace(orderNumber))
        {
            return WalletOrderPaymentResult.BadRequest("OrderNumber is required.");
        }

        var normalizedKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim();
        if (normalizedKey != null && normalizedKey.Length > 100)
        {
            return WalletOrderPaymentResult.BadRequest("IdempotencyKey cannot exceed 100 characters.");
        }

        // 3. Begin single atomic PostgreSQL database transaction
        IDbContextTransaction? tx = null;
        if (_db.Database.IsRelational())
        {
            tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        }

        try
        {
            // 4. Acquire pessimistic row-level lock on the Order entity in PostgreSQL
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
                return WalletOrderPaymentResult.NotFound($"Order '{orderNumber}' was not found.");
            }

            // 5. Strict customer ownership check — returns 404 to prevent enumeration
            if (!string.Equals(order.CustomerId, customerId, StringComparison.Ordinal))
            {
                if (tx != null) await tx.RollbackAsync(ct);
                return WalletOrderPaymentResult.NotFound($"Order '{orderNumber}' was not found.");
            }

            // 6. Currency and amount validation
            if (!string.Equals(order.Currency, "PKR", StringComparison.OrdinalIgnoreCase))
            {
                if (tx != null) await tx.RollbackAsync(ct);
                return WalletOrderPaymentResult.BadRequest($"Only PKR orders can be paid with wallet balance. Order currency is '{order.Currency}'.");
            }

            if (order.TotalAmount <= 0)
            {
                if (tx != null) await tx.RollbackAsync(ct);
                return WalletOrderPaymentResult.BadRequest("Order total amount must be greater than zero.");
            }

            // 7. Check if Order is already Paid (Handle Idempotent Replays and Conflicts)
            if (order.Status == OrderStatus.Paid)
            {
                // Look for the Paid payment record associated with this order
                var existingPaidPayment = await _db.Payments
                    .FirstOrDefaultAsync(p => p.OrderId == order.Id && p.Status == PaymentStatus.Paid, ct);

                if (existingPaidPayment != null && normalizedKey != null &&
                    string.Equals(existingPaidPayment.IdempotencyKey, normalizedKey, StringComparison.Ordinal))
                {
                    // Safe idempotent replay with matching key
                    if (tx != null) await tx.RollbackAsync(ct);
                    var currentBalance = await _walletService.GetBalanceAsync(customerId, "PKR", ct) ?? 0m;
                    var replayDto = new CustomerOrderPaymentDto
                    {
                        PaymentNumber = existingPaidPayment.PaymentNumber,
                        OrderNumber = order.OrderNumber,
                        OrderStatus = order.Status.ToString(),
                        PaymentStatus = existingPaidPayment.Status.ToString(),
                        AmountPaid = existingPaidPayment.Amount,
                        Currency = existingPaidPayment.Currency,
                        PaymentMethod = existingPaidPayment.PaymentMethodCode,
                        WalletRemainingBalance = currentBalance,
                        PaidAt = existingPaidPayment.PaidAt ?? existingPaidPayment.CreatedAt
                    };

                    return WalletOrderPaymentResult.Success(replayDto, "Order payment already processed successfully.", isReplay: true);
                }

                // If order is already paid, another request with a different key (or no key) must return 409 Conflict
                if (tx != null) await tx.RollbackAsync(ct);
                return WalletOrderPaymentResult.Conflict($"Order '{orderNumber}' has already been paid.");
            }

            // 8. Order status must be PendingPayment
            if (order.Status != OrderStatus.PendingPayment)
            {
                if (tx != null) await tx.RollbackAsync(ct);
                return WalletOrderPaymentResult.BadRequest($"Cannot pay order in '{order.Status}' status. Only PendingPayment orders can be paid.");
            }

            // 9. Pre-check idempotency key usage across other orders or existing attempts for this customer
            if (normalizedKey != null)
            {
                var existingKeyPayment = await _db.Payments
                    .Include(p => p.Order)
                    .FirstOrDefaultAsync(p => p.IdempotencyKey == normalizedKey && p.Order != null && p.Order.CustomerId == customerId, ct);

                if (existingKeyPayment != null)
                {
                    if (existingKeyPayment.OrderId != order.Id)
                    {
                        if (tx != null) await tx.RollbackAsync(ct);
                        return WalletOrderPaymentResult.Conflict($"Idempotency key '{normalizedKey}' has already been used for a different order payment.");
                    }

                    if (existingKeyPayment.Status == PaymentStatus.Paid)
                    {
                        if (tx != null) await tx.RollbackAsync(ct);
                        var currentBalance = await _walletService.GetBalanceAsync(customerId, "PKR", ct) ?? 0m;
                        var replayDto = new CustomerOrderPaymentDto
                        {
                            PaymentNumber = existingKeyPayment.PaymentNumber,
                            OrderNumber = order.OrderNumber,
                            OrderStatus = order.Status.ToString(),
                            PaymentStatus = existingKeyPayment.Status.ToString(),
                            AmountPaid = existingKeyPayment.Amount,
                            Currency = existingKeyPayment.Currency,
                            PaymentMethod = existingKeyPayment.PaymentMethodCode,
                            WalletRemainingBalance = currentBalance,
                            PaidAt = existingKeyPayment.PaidAt ?? existingKeyPayment.CreatedAt
                        };
                        return WalletOrderPaymentResult.Success(replayDto, "Order payment already processed successfully.", isReplay: true);
                    }
                }
            }

            // 10. Debit the customer wallet using stable operation key derived from OrderId
            var operationKey = $"WALLET_ORDER:{order.Id}";
            var debitReq = new WalletDebitRequest
            {
                CustomerId = customerId,
                Amount = order.TotalAmount,
                Currency = "PKR",
                OperationKey = operationKey,
                SourceType = WalletTransactionSource.OrderPayment,
                SourceReferenceId = order.OrderNumber,
                Description = $"Payment for order {order.OrderNumber}",
                CreatedByUserId = customerId
            };

            var debitResult = await _walletService.DebitAsync(debitReq, ct);

            if (debitResult.Status == WalletOperationStatus.InsufficientFunds)
            {
                if (tx != null) await tx.RollbackAsync(ct);
                return WalletOrderPaymentResult.InsufficientFunds(debitResult.Message);
            }

            if (debitResult.Status == WalletOperationStatus.Conflict)
            {
                if (tx != null) await tx.RollbackAsync(ct);
                return WalletOrderPaymentResult.Conflict($"Payment for order '{orderNumber}' has already been debited.");
            }

            if (debitResult.Status != WalletOperationStatus.Success)
            {
                if (tx != null) await tx.RollbackAsync(ct);
                return WalletOrderPaymentResult.BadRequest(debitResult.Message);
            }

            // Verify that a concurrent request from the same customer didn't commit with this same key while waiting for wallet lock
            if (normalizedKey != null)
            {
                var concurrentKeyConflict = await _db.Payments
                    .AnyAsync(p => p.IdempotencyKey == normalizedKey && p.Order != null && p.Order.CustomerId == customerId && p.OrderId != order.Id, ct);
                if (concurrentKeyConflict)
                {
                    if (tx != null) await tx.RollbackAsync(ct);
                    return WalletOrderPaymentResult.Conflict($"Idempotency key '{normalizedKey}' has already been used for a different order payment.");
                }
            }

            // 11. Create and persist Payment record and PaymentStatusHistory within the transaction
            var paymentNumber = _paymentNumberGenerator.GeneratePaymentNumber();
            var now = DateTime.UtcNow;

            var payment = new Payment
            {
                PaymentNumber = paymentNumber,
                Purpose = PaymentPurpose.OrderPurchase,
                OrderId = order.Id,
                WalletId = null, // Targets Order, not Wallet
                Amount = order.TotalAmount,
                Currency = "PKR",
                PaymentMethodCode = "WALLET",
                Status = PaymentStatus.Paid,
                PaidAt = now,
                IdempotencyKey = normalizedKey,
                ProviderTransactionId = $"WALLET:{debitResult.Transaction!.Id}",
                CreatedAt = now,
                UpdatedAt = now
            };

            // Status history: record initial initiation and confirmed Paid state
            payment.StatusHistory.Add(new PaymentStatusHistory
            {
                PreviousStatus = null,
                NewStatus = PaymentStatus.Pending,
                Reason = "Payment initiated via customer PKR wallet balance.",
                ChangedByUserId = customerId,
                ChangedAt = now
            });

            payment.StatusHistory.Add(new PaymentStatusHistory
            {
                PreviousStatus = PaymentStatus.Pending,
                NewStatus = PaymentStatus.Paid,
                Reason = $"Successfully debited {order.TotalAmount:F4} PKR from customer wallet (Transaction: {debitResult.Transaction.Id}).",
                ChangedByUserId = customerId,
                ChangedAt = now
            });

            _db.Payments.Add(payment);

            // 12. Advance Order status from PendingPayment to Paid and append OrderStatusHistory
            order.Status = OrderStatus.Paid;
            order.UpdatedAt = now;

            var orderHistory = new OrderStatusHistory
            {
                OrderId = order.Id,
                PreviousStatus = OrderStatus.PendingPayment,
                NewStatus = OrderStatus.Paid,
                Reason = $"Order paid via customer PKR wallet balance. Payment: {payment.PaymentNumber}.",
                ChangedByUserId = customerId,
                ChangedAt = now
            };
            _db.OrderStatusHistories.Add(orderHistory);

            // 13. Commit everything together atomically
            await _db.SaveChangesAsync(ct);

            if (tx != null)
            {
                await tx.CommitAsync(ct);
            }

            _logger.LogInformation(
                "Order {OrderNumber} paid successfully via wallet for customer {CustomerId}. Payment: {PaymentNumber}, Amount: {Amount} PKR",
                order.OrderNumber, customerId, payment.PaymentNumber, payment.Amount);

            var confirmationDto = new CustomerOrderPaymentDto
            {
                PaymentNumber = payment.PaymentNumber,
                OrderNumber = order.OrderNumber,
                OrderStatus = order.Status.ToString(),
                PaymentStatus = payment.Status.ToString(),
                AmountPaid = payment.Amount,
                Currency = payment.Currency,
                PaymentMethod = payment.PaymentMethodCode,
                WalletRemainingBalance = debitResult.Wallet!.Balance,
                PaidAt = now
            };

            return WalletOrderPaymentResult.Success(confirmationDto, "Order paid successfully via wallet balance.");
        }
        catch (DbUpdateException ex)
        {
            if (tx != null) await tx.RollbackAsync(ct);

            var pgEx = ex.InnerException as PostgresException;
            if (pgEx != null)
            {
                if (pgEx.SqlState == "23505")
                {
                    if (pgEx.ConstraintName != null && pgEx.ConstraintName.Contains("IX_Payments_Paid_OrderId"))
                    {
                        return WalletOrderPaymentResult.Conflict($"Order '{orderNumber}' has already been paid.");
                    }

                    if (pgEx.ConstraintName != null && pgEx.ConstraintName.Contains("IX_Payments_OrderId_IdempotencyKey"))
                    {
                        return WalletOrderPaymentResult.Conflict($"Idempotency key '{normalizedKey}' has already been used for this order.");
                    }

                    if (pgEx.ConstraintName != null && pgEx.ConstraintName.Contains("IX_WalletTransactions_WalletId_OperationKey"))
                    {
                        return WalletOrderPaymentResult.Conflict($"Payment for order '{orderNumber}' has already been debited.");
                    }
                }

                if (pgEx.SqlState == "40P01" || pgEx.SqlState == "55P03")
                {
                    _logger.LogWarning(ex, "Concurrency deadlock or lock timeout during wallet payment for order {OrderNumber}", orderNumber);
                    return WalletOrderPaymentResult.Conflict("The transaction could not be completed due to concurrent conflict. Please retry your request.");
                }
            }

            _logger.LogError(ex, "DbUpdateException during wallet payment for order {OrderNumber}", orderNumber);
            throw;
        }
        catch (Exception ex)
        {
            if (tx != null) await tx.RollbackAsync(ct);

            var pgEx = ex as PostgresException ?? ex.InnerException as PostgresException;
            if (pgEx != null && (pgEx.SqlState == "40P01" || pgEx.SqlState == "55P03"))
            {
                _logger.LogWarning(ex, "Concurrency deadlock or lock timeout during wallet payment for order {OrderNumber}", orderNumber);
                return WalletOrderPaymentResult.Conflict("The transaction could not be completed due to concurrent conflict. Please retry your request.");
            }

            _logger.LogError(ex, "Unexpected error during wallet payment for order {OrderNumber}", orderNumber);
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
}
