using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using WebApiTgBot.Infrastructure.Data;
using WebApiTgBot.Modules.Fulfillments.Models;
using WebApiTgBot.Modules.Fulfillments.Models.DTOs;
using WebApiTgBot.Modules.Orders.Models;
using WebApiTgBot.Modules.Payments.Models;

namespace WebApiTgBot.Modules.Fulfillments.Services;

/// <summary>
/// Internal fulfillment service managing line item fulfillment lifecycle,
/// supplier attempt tracking, eligibility enforcement, and safe fallback rules.
/// </summary>
public class FulfillmentService : IFulfillmentService
{
    private readonly AppDbContext _db;
    private readonly ILogger<FulfillmentService> _logger;

    public FulfillmentService(AppDbContext db, ILogger<FulfillmentService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<FulfillmentInitializationResult> InitializeFulfillmentForOrderItemAsync(
        long orderId,
        long orderItemId,
        CancellationToken cancellationToken = default)
    {
        var order = await _db.Orders
            .Include(o => o.Items)
            .Include(o => o.Payments)
            .FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);

        if (order == null)
        {
            _logger.LogWarning("Fulfillment initialization failed: Order {OrderId} not found.", orderId);
            return FulfillmentInitializationResult.Failed(FulfillmentInitializationStatus.OrderNotFound, $"Order {orderId} not found.");
        }

        // Eligibility check 1: Order status must be Paid or Processing
        if (order.Status != OrderStatus.Paid && order.Status != OrderStatus.Processing)
        {
            _logger.LogWarning("Fulfillment initialization rejected: Order {OrderNumber} is in status {Status}; must be Paid or Processing.",
                order.OrderNumber, order.Status);
            return FulfillmentInitializationResult.Failed(FulfillmentInitializationStatus.UnpaidOrderRejected,
                $"Order {order.OrderNumber} is in status {order.Status}. Fulfillment can only initialize for Paid or Processing orders.");
        }

        // Eligibility check 2: Trusted Paid payment of purpose OrderPurchase matching amount and currency
        var verifiedPaidPayment = order.Payments.FirstOrDefault(p =>
            p.Purpose == PaymentPurpose.OrderPurchase &&
            p.Status == PaymentStatus.Paid);

        if (verifiedPaidPayment == null)
        {
            _logger.LogWarning("Fulfillment initialization rejected: Order {OrderNumber} has no verified Paid payment.", order.OrderNumber);
            return FulfillmentInitializationResult.Failed(FulfillmentInitializationStatus.UnpaidOrderRejected,
                $"Order {order.OrderNumber} has no verified Paid payment on file.");
        }

        if (verifiedPaidPayment.Amount != order.TotalAmount || !string.Equals(verifiedPaidPayment.Currency, order.Currency, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Fulfillment initialization rejected: Order {OrderNumber} payment amount ({PayAmt} {PayCurr}) does not match order ({OrdAmt} {OrdCurr}).",
                order.OrderNumber, verifiedPaidPayment.Amount, verifiedPaidPayment.Currency, order.TotalAmount, order.Currency);
            return FulfillmentInitializationResult.Failed(FulfillmentInitializationStatus.PaymentMismatch,
                $"Verified payment amount ({verifiedPaidPayment.Amount} {verifiedPaidPayment.Currency}) does not match order total ({order.TotalAmount} {order.Currency}).");
        }

        // Check line item belongs to this order
        var orderItem = order.Items.FirstOrDefault(i => i.Id == orderItemId);
        if (orderItem == null)
        {
            _logger.LogWarning("Fulfillment initialization failed: OrderItem {OrderItemId} does not belong to Order {OrderNumber}.",
                orderItemId, order.OrderNumber);
            return FulfillmentInitializationResult.Failed(FulfillmentInitializationStatus.OrderItemNotFound,
                $"Order item {orderItemId} not found in order {order.OrderNumber}.");
        }

        // Check idempotency: already initialized
        var existingFulfillment = await _db.Fulfillments
            .Include(f => f.Attempts)
            .FirstOrDefaultAsync(f => f.OrderItemId == orderItemId, cancellationToken);

        if (existingFulfillment != null)
        {
            _logger.LogInformation("Fulfillment already initialized for OrderItem {OrderItemId}. Returning existing fulfillment {FulfillmentId}.",
                orderItemId, existingFulfillment.Id);
            return FulfillmentInitializationResult.AlreadyExists(existingFulfillment);
        }

        // Initialize new fulfillment record
        var now = DateTime.UtcNow;
        var fulfillment = new Fulfillment
        {
            OrderId = order.Id,
            OrderItemId = orderItem.Id,
            Status = FulfillmentStatus.NotStarted,
            FulfillmentMethod = FulfillmentMethod.Unassigned,
            QuantityRequested = orderItem.Quantity,
            QuantityFulfilled = 0,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.Fulfillments.Add(fulfillment);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Initialized Fulfillment {FulfillmentId} for Order {OrderNumber}, Item {OrderItemId}.",
                fulfillment.Id, order.OrderNumber, orderItem.Id);

            return FulfillmentInitializationResult.Succeeded(fulfillment);
        }
        catch (DbUpdateException ex)
        {
            var pgEx = ex.InnerException as PostgresException ?? ex.GetBaseException() as PostgresException;
            if (pgEx != null && pgEx.SqlState == "23505" &&
                (pgEx.ConstraintName?.Contains("OrderItemId", StringComparison.OrdinalIgnoreCase) == true ||
                 pgEx.Message.Contains("OrderItemId", StringComparison.OrdinalIgnoreCase) ||
                 pgEx.Detail?.Contains("OrderItemId", StringComparison.OrdinalIgnoreCase) == true))
            {
                _logger.LogWarning("Concurrent fulfillment initialization race detected for OrderItem {OrderItemId}. Recovering existing record.", orderItemId);
                _db.ChangeTracker.Clear();

                var winningFulfillment = await _db.Fulfillments
                    .AsNoTracking()
                    .Include(f => f.Attempts)
                    .FirstOrDefaultAsync(f => f.OrderItemId == orderItemId, cancellationToken);

                if (winningFulfillment != null)
                {
                    return FulfillmentInitializationResult.AlreadyExists(winningFulfillment);
                }
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async Task<Fulfillment?> GetFulfillmentByOrderItemIdAsync(long orderItemId, CancellationToken cancellationToken = default)
    {
        return await _db.Fulfillments
            .Include(f => f.Order)
            .Include(f => f.OrderItem)
            .Include(f => f.Attempts)
                .ThenInclude(a => a.VendorProduct)
                    .ThenInclude(vp => vp!.Vendor)
            .FirstOrDefaultAsync(f => f.OrderItemId == orderItemId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Fulfillment>> GetFulfillmentsByOrderIdAsync(long orderId, CancellationToken cancellationToken = default)
    {
        return await _db.Fulfillments
            .Include(f => f.OrderItem)
            .Include(f => f.Attempts)
                .ThenInclude(a => a.VendorProduct)
                    .ThenInclude(vp => vp!.Vendor)
            .Where(f => f.OrderId == orderId)
            .OrderBy(f => f.Id)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<FulfillmentAttemptResult> CreateAttemptAsync(
        long fulfillmentId,
        int? vendorProductId,
        CancellationToken cancellationToken = default)
    {
        var fulfillment = await _db.Fulfillments
            .Include(f => f.Attempts)
            .FirstOrDefaultAsync(f => f.Id == fulfillmentId, cancellationToken);

        if (fulfillment == null)
        {
            return FulfillmentAttemptResult.Failed(FulfillmentAttemptOperationStatus.FulfillmentNotFound,
                $"Fulfillment record {fulfillmentId} not found.");
        }

        // If vendor product specified, verify it exists and matches the purchased OrderItem.ProductId
        if (vendorProductId.HasValue)
        {
            var vendorProduct = await _db.VendorProducts
                .Include(vp => vp.Vendor)
                .FirstOrDefaultAsync(vp => vp.Id == vendorProductId.Value, cancellationToken);

            if (vendorProduct == null)
            {
                return FulfillmentAttemptResult.Failed(FulfillmentAttemptOperationStatus.InvalidVendorProduct,
                    $"Vendor product mapping {vendorProductId.Value} not found.");
            }

            // Always resolve OrderItem — never rely on a nullable navigation property to bypass validation
            var orderItem = fulfillment.OrderItem ?? await _db.OrderItems
                .FirstOrDefaultAsync(oi => oi.Id == fulfillment.OrderItemId, cancellationToken);

            if (orderItem == null)
            {
                _logger.LogError("OrderItem {OrderItemId} for Fulfillment {FulfillmentId} could not be resolved.", fulfillment.OrderItemId, fulfillment.Id);
                return FulfillmentAttemptResult.Failed(FulfillmentAttemptOperationStatus.InvalidVendorProduct,
                    $"Purchased order item {fulfillment.OrderItemId} could not be resolved for fulfillment {fulfillment.Id}.");
            }

            // CRITICAL CHECK: VendorProduct.ProductId must match the purchased OrderItem.ProductId
            if (vendorProduct.ProductId != orderItem.ProductId)
            {
                _logger.LogWarning("Vendor product mismatch: VendorProduct {VendorProductId} maps to Product {VpProductId}, but OrderItem {OrderItemId} purchased Product {OiProductId}.",
                    vendorProduct.Id, vendorProduct.ProductId, orderItem.Id, orderItem.ProductId);

                return FulfillmentAttemptResult.Failed(FulfillmentAttemptOperationStatus.InvalidVendorProduct,
                    $"Vendor product mapping {vendorProductId.Value} is for Product ID {vendorProduct.ProductId}, but OrderItem {orderItem.Id} purchased Product ID {orderItem.ProductId}. Mismatched product mappings are strictly rejected.");
            }
        }

        // CRITICAL SAFEGUARD: Check unconfirmed outcome or incomplete previous attempt
        if (fulfillment.Attempts.Any())
        {
            var latestAttempt = fulfillment.Attempts.OrderByDescending(a => a.AttemptNumber).First();
            if (latestAttempt.Status == FulfillmentAttemptStatus.Unknown ||
                latestAttempt.Status == FulfillmentAttemptStatus.RequiresReconciliation)
            {
                _logger.LogWarning("Attempt creation blocked: Fulfillment {FulfillmentId} has previous attempt {AttemptKey} with unconfirmed status {Status}.",
                    fulfillmentId, latestAttempt.AttemptKey, latestAttempt.Status);

                return FulfillmentAttemptResult.Failed(
                    FulfillmentAttemptOperationStatus.FallbackNotAllowedDueToUnconfirmedOutcome,
                    $"Cannot create a subsequent attempt while previous attempt '{latestAttempt.AttemptKey}' has unconfirmed status '{latestAttempt.Status}'. Status reconciliation or administrative resolution is strictly required before fallback.");
            }

            if (latestAttempt.Status == FulfillmentAttemptStatus.Submitted)
            {
                _logger.LogWarning("Attempt creation blocked: Fulfillment {FulfillmentId} has previous attempt {AttemptKey} still in Submitted status.",
                    fulfillmentId, latestAttempt.AttemptKey);

                return FulfillmentAttemptResult.Failed(
                    FulfillmentAttemptOperationStatus.FallbackNotAllowedDueToUnconfirmedOutcome,
                    $"Cannot create a subsequent attempt while previous attempt '{latestAttempt.AttemptKey}' is currently Submitted and awaiting supplier response.");
            }

            if (latestAttempt.Status == FulfillmentAttemptStatus.Succeeded)
            {
                _logger.LogWarning("Attempt creation blocked: Fulfillment {FulfillmentId} already has a Succeeded attempt {AttemptKey}.",
                    fulfillmentId, latestAttempt.AttemptKey);

                return FulfillmentAttemptResult.Failed(
                    FulfillmentAttemptOperationStatus.AttemptAlreadyTerminal,
                    $"Cannot create a subsequent attempt because attempt '{latestAttempt.AttemptKey}' has already succeeded.");
            }
        }

        var nextNumber = (fulfillment.Attempts.Max(a => (int?)a.AttemptNumber) ?? 0) + 1;
        var attemptKey = $"ATT-{fulfillment.Id}-{nextNumber}";
        var now = DateTime.UtcNow;

        var attempt = new FulfillmentAttempt
        {
            FulfillmentId = fulfillment.Id,
            AttemptNumber = nextNumber,
            AttemptKey = attemptKey,
            VendorProductId = vendorProductId,
            Status = FulfillmentAttemptStatus.NotStarted,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.FulfillmentAttempts.Add(attempt);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created FulfillmentAttempt {AttemptKey} (Number {AttemptNumber}) for Fulfillment {FulfillmentId}.",
            attemptKey, nextNumber, fulfillment.Id);

        return FulfillmentAttemptResult.Succeeded(attempt);
    }

    /// <inheritdoc />
    public async Task<FulfillmentAttemptResult> RecordAttemptOutcomeAsync(
        long attemptId,
        FulfillmentAttemptStatus outcomeStatus,
        FulfillmentFailureCategory? failureCategory = null,
        string? safeErrorSummary = null,
        string? externalVendorOrderId = null,
        CancellationToken cancellationToken = default)
    {
        var attempt = await _db.FulfillmentAttempts
            .Include(a => a.Fulfillment)
            .FirstOrDefaultAsync(a => a.Id == attemptId, cancellationToken);

        if (attempt == null)
        {
            return FulfillmentAttemptResult.Failed(FulfillmentAttemptOperationStatus.FulfillmentNotFound,
                $"Fulfillment attempt {attemptId} not found.");
        }

        if (FulfillmentAttemptRules.IsAttemptTerminal(attempt.Status))
        {
            return FulfillmentAttemptResult.Failed(FulfillmentAttemptOperationStatus.AttemptAlreadyTerminal,
                $"Attempt {attempt.AttemptKey} is already in terminal status '{attempt.Status}' and cannot be modified.");
        }

        var now = DateTime.UtcNow;
        attempt.Status = outcomeStatus;
        attempt.FailureCategory = failureCategory;
        attempt.SafeErrorSummary = string.IsNullOrWhiteSpace(safeErrorSummary)
            ? null
            : (safeErrorSummary.Length > 500 ? safeErrorSummary.Substring(0, 500) : safeErrorSummary);
        attempt.ExternalVendorOrderId = string.IsNullOrWhiteSpace(externalVendorOrderId) ? null : externalVendorOrderId.Trim();
        attempt.UpdatedAt = now;

        if (outcomeStatus == FulfillmentAttemptStatus.Submitted && attempt.SubmittedAt == null)
        {
            attempt.SubmittedAt = now;
        }

        if (outcomeStatus == FulfillmentAttemptStatus.Succeeded)
        {
            attempt.CompletedAt = now;

            // Advance fulfillment status if valid
            if (FulfillmentStatusTransitions.CanTransition(attempt.Fulfillment.Status, FulfillmentStatus.InProgress))
            {
                attempt.Fulfillment.Status = FulfillmentStatus.InProgress;
                attempt.Fulfillment.UpdatedAt = now;
            }
        }
        else if (outcomeStatus == FulfillmentAttemptStatus.Unknown || outcomeStatus == FulfillmentAttemptStatus.RequiresReconciliation)
        {
            // Set fulfillment to RequiresReview
            if (FulfillmentStatusTransitions.CanTransition(attempt.Fulfillment.Status, FulfillmentStatus.RequiresReview))
            {
                attempt.Fulfillment.Status = FulfillmentStatus.RequiresReview;
                attempt.Fulfillment.UpdatedAt = now;
            }
        }

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Recorded outcome '{Status}' for FulfillmentAttempt {AttemptKey}.",
            outcomeStatus, attempt.AttemptKey);

        return FulfillmentAttemptResult.Succeeded(attempt);
    }

    /// <inheritdoc />
    public bool CanFallbackAfterAttempt(FulfillmentAttemptStatus status)
    {
        return FulfillmentAttemptRules.CanFallbackToNextVendor(status);
    }
}
