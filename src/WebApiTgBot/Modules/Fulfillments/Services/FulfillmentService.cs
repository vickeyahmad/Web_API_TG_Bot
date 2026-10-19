using System.Data;
using Microsoft.EntityFrameworkCore;
using WebApiTgBot.Common.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using WebApiTgBot.Infrastructure.Data;
using WebApiTgBot.Modules.Fulfillments.Models;
using WebApiTgBot.Modules.Fulfillments.Models.DTOs;
using WebApiTgBot.Modules.Fulfillments.Options;
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
    private readonly IOptions<ManualFulfillmentOptions> _manualFulfillmentOptions;

    public FulfillmentService(
        AppDbContext db,
        ILogger<FulfillmentService> logger,
        IOptions<ManualFulfillmentOptions>? manualFulfillmentOptions = null)
    {
        _db = db;
        _logger = logger;
        _manualFulfillmentOptions = manualFulfillmentOptions ?? Microsoft.Extensions.Options.Options.Create(new ManualFulfillmentOptions());
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

    /// <inheritdoc />
    public async Task<ManualFulfillmentOperationResult> StartManualFulfillmentAsync(
        string orderNumber,
        long orderItemId,
        string adminUserId,
        CancellationToken cancellationToken = default)
    {
        // 1. Production safety feature gate
        if (!_manualFulfillmentOptions.Value.Enabled)
        {
            _logger.LogWarning("Manual fulfillment start rejected: feature is disabled by server configuration.");
            return ManualFulfillmentOperationResult.Forbidden("Manual fulfillment is currently disabled by server configuration.");
        }

        if (string.IsNullOrWhiteSpace(adminUserId))
        {
            return ManualFulfillmentOperationResult.BadRequest("Admin user ID is required.");
        }

        await using var tx = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            : null;

        try
        {
            // 2. Lock Order row to maintain invariant lock order (Order first)
            Order? order;
            if (_db.Database.IsRelational())
            {
                order = await _db.Orders
                    .FromSqlInterpolated($"SELECT * FROM \"Orders\" WHERE \"OrderNumber\" = {orderNumber.Trim()} FOR UPDATE")
                    .FirstOrDefaultAsync(cancellationToken);
            }
            else
            {
                order = await _db.Orders
                    .FirstOrDefaultAsync(o => o.OrderNumber == orderNumber.Trim(), cancellationToken);
            }

            if (order == null)
            {
                if (tx != null) await tx.RollbackAsync(cancellationToken);
                return ManualFulfillmentOperationResult.NotFound($"Order '{orderNumber}' was not found.");
            }

            // 3. Order status check: Order must be in Processing status
            if (order.Status == OrderStatus.PendingPayment)
            {
                if (tx != null) await tx.RollbackAsync(cancellationToken);
                return ManualFulfillmentOperationResult.BadRequest($"Cannot start manual fulfillment for an unpaid order. Order '{orderNumber}' is in PendingPayment status.");
            }

            if (order.Status == OrderStatus.Paid)
            {
                if (tx != null) await tx.RollbackAsync(cancellationToken);
                return ManualFulfillmentOperationResult.Conflict($"Order '{orderNumber}' is in Paid status. Administrator must start order processing before initiating manual fulfillment.");
            }

            if (order.Status != OrderStatus.Processing)
            {
                if (tx != null) await tx.RollbackAsync(cancellationToken);
                return ManualFulfillmentOperationResult.Conflict($"Cannot start manual fulfillment for order '{orderNumber}' in '{order.Status}' status. Order must be in Processing status.");
            }

            // 4. Verify trusted Paid OrderPurchase payment record matching order total and currency
            var paidPayment = await _db.Payments
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.OrderId == order.Id &&
                                          p.Purpose == PaymentPurpose.OrderPurchase &&
                                          p.Status == PaymentStatus.Paid, cancellationToken);

            if (paidPayment == null)
            {
                if (tx != null) await tx.RollbackAsync(cancellationToken);
                return ManualFulfillmentOperationResult.BadRequest($"Order '{orderNumber}' has no verified Paid payment record.");
            }

            if (paidPayment.Amount != order.TotalAmount || paidPayment.Currency != order.Currency)
            {
                if (tx != null) await tx.RollbackAsync(cancellationToken);
                return ManualFulfillmentOperationResult.BadRequest($"Payment verification failed: Paid amount ({paidPayment.Amount} {paidPayment.Currency}) does not match order total ({order.TotalAmount} {order.Currency}).");
            }

            // 5. Verify purchased OrderItem belongs to the specified Order
            var orderItem = await _db.OrderItems
                .Include(oi => oi.Product)
                .FirstOrDefaultAsync(oi => oi.Id == orderItemId && oi.OrderId == order.Id, cancellationToken);

            if (orderItem == null)
            {
                if (tx != null) await tx.RollbackAsync(cancellationToken);
                return ManualFulfillmentOperationResult.NotFound($"Order item {orderItemId} does not belong to order '{orderNumber}'.");
            }

            // 6. Safe initialization / retrieval of Fulfillment record for this line item
            var fulfillment = await _db.Fulfillments
                .Include(f => f.Attempts)
                .FirstOrDefaultAsync(f => f.OrderItemId == orderItemId, cancellationToken);

            if (fulfillment == null)
            {
                fulfillment = new Fulfillment
                {
                    OrderId = order.Id,
                    OrderItemId = orderItem.Id,
                    Status = FulfillmentStatus.NotStarted,
                    FulfillmentMethod = FulfillmentMethod.Manual,
                    QuantityRequested = orderItem.Quantity,
                    QuantityFulfilled = 0,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                _db.Fulfillments.Add(fulfillment);
                await _db.SaveChangesAsync(cancellationToken);
            }

            // 7. Prevent duplicate delivery: Examine existing fulfillment state and attempts
            if (fulfillment.Status == FulfillmentStatus.Completed)
            {
                if (tx != null) await tx.RollbackAsync(cancellationToken);
                return ManualFulfillmentOperationResult.Conflict($"Fulfillment for order item {orderItemId} is already Completed.");
            }

            if (fulfillment.Status == FulfillmentStatus.Failed)
            {
                if (tx != null) await tx.RollbackAsync(cancellationToken);
                return ManualFulfillmentOperationResult.Conflict($"Fulfillment for order item {orderItemId} is in a terminal Failed status.");
            }

            // Check existing attempts
            if (fulfillment.Attempts.Any())
            {
                // CRITICAL SAFEGUARD: Reject if ANY attempt has unconfirmed status (Unknown or RequiresReconciliation)
                if (fulfillment.Attempts.Any(a => a.Status == FulfillmentAttemptStatus.Unknown || a.Status == FulfillmentAttemptStatus.RequiresReconciliation))
                {
                    if (tx != null) await tx.RollbackAsync(cancellationToken);
                    _logger.LogWarning("Manual fulfillment start blocked: Fulfillment {FulfillmentId} has an unresolved attempt with unconfirmed outcome.", fulfillment.Id);
                    return ManualFulfillmentOperationResult.Conflict("Cannot start manual fulfillment: A previous supplier attempt has an unconfirmed status (Unknown or RequiresReconciliation). Status reconciliation or administrative investigation is strictly required before any subsequent fulfillment action to prevent duplicate delivery.");
                }

                // Reject if any attempt is actively processing (Submitted)
                if (fulfillment.Attempts.Any(a => a.Status == FulfillmentAttemptStatus.Submitted))
                {
                    if (tx != null) await tx.RollbackAsync(cancellationToken);
                    return ManualFulfillmentOperationResult.Conflict("Cannot start manual fulfillment: An attempt is already actively processing for this line item.");
                }

                // Reject if any attempt already succeeded
                if (fulfillment.Attempts.Any(a => a.Status == FulfillmentAttemptStatus.Succeeded))
                {
                    if (tx != null) await tx.RollbackAsync(cancellationToken);
                    return ManualFulfillmentOperationResult.Conflict("Cannot start manual fulfillment: A previous attempt has already succeeded for this line item.");
                }
            }

            // 8. Validate state transition for Fulfillment
            var previousStatus = fulfillment.Status;
            if (!FulfillmentStatusTransitions.CanTransition(previousStatus, FulfillmentStatus.InProgress))
            {
                if (tx != null) await tx.RollbackAsync(cancellationToken);
                return ManualFulfillmentOperationResult.Conflict($"Transition from '{previousStatus}' to 'InProgress' is not permitted by fulfillment lifecycle rules.");
            }

            // 9. Assign FulfillmentMethod.Manual, set Status = InProgress
            fulfillment.FulfillmentMethod = FulfillmentMethod.Manual;
            fulfillment.Status = FulfillmentStatus.InProgress;
            fulfillment.UpdatedAt = DateTime.UtcNow;

            // 10. Create tracked manual FulfillmentAttempt with VendorProductId = null
            var nextNumber = (fulfillment.Attempts.Max(a => (int?)a.AttemptNumber) ?? 0) + 1;
            var attemptKey = $"ATT-{fulfillment.Id}-{nextNumber}";
            var now = DateTime.UtcNow;

            var attempt = new FulfillmentAttempt
            {
                FulfillmentId = fulfillment.Id,
                AttemptNumber = nextNumber,
                AttemptKey = attemptKey,
                VendorProductId = null, // Strictly null for manual fulfillment!
                Status = FulfillmentAttemptStatus.Submitted,
                InitiatedByUserId = adminUserId,
                CreatedAt = now,
                UpdatedAt = now,
                SubmittedAt = now
            };
            _db.FulfillmentAttempts.Add(attempt);

            // 11. Record FulfillmentStatusHistory
            var statusHistory = new FulfillmentStatusHistory
            {
                FulfillmentId = fulfillment.Id,
                PreviousStatus = previousStatus,
                NewStatus = FulfillmentStatus.InProgress,
                Reason = "Administrator started manual fulfillment.",
                ChangedByUserId = adminUserId,
                ChangedAt = now
            };
            _db.FulfillmentStatusHistories.Add(statusHistory);

            await _db.SaveChangesAsync(cancellationToken);
            if (tx != null) await tx.CommitAsync(cancellationToken);

            _logger.LogInformation("Administrator {AdminUserId} started manual fulfillment for Order {OrderNumber}, Item {OrderItemId}, Fulfillment {FulfillmentId}, Attempt {AttemptKey}.",
                adminUserId, orderNumber, orderItemId, fulfillment.Id, attemptKey);

            var dto = await BuildAdminFulfillmentDtoAsync(fulfillment.Id, cancellationToken);
            return ManualFulfillmentOperationResult.Success(dto!, "Manual fulfillment started successfully.");
        }
        catch (Exception ex)
        {
            if (tx != null) await tx.RollbackAsync(cancellationToken);
            _logger.LogError(ex, "Unexpected error starting manual fulfillment for Order {OrderNumber}, Item {OrderItemId}.", orderNumber, orderItemId);
            return ManualFulfillmentOperationResult.BadRequest("An error occurred while starting manual fulfillment. Operation rolled back safely.");
        }
    }

    /// <inheritdoc />
    public async Task<ManualFulfillmentOperationResult> CompleteManualFulfillmentAsync(
        string orderNumber,
        long orderItemId,
        string adminUserId,
        AdminCompleteManualFulfillmentRequest request,
        CancellationToken cancellationToken = default)
    {
        // 1. Production safety feature gate
        if (!_manualFulfillmentOptions.Value.Enabled)
        {
            _logger.LogWarning("Manual fulfillment complete rejected: feature is disabled by server configuration.");
            return ManualFulfillmentOperationResult.Forbidden("Manual fulfillment is currently disabled by server configuration.");
        }

        if (string.IsNullOrWhiteSpace(adminUserId))
        {
            return ManualFulfillmentOperationResult.BadRequest("Admin user ID is required.");
        }

        // 2. Validate request parameters & sanitize text fields
        if (!request.ConfirmDelivered)
        {
            return ManualFulfillmentOperationResult.BadRequest("Explicit confirmation of delivery (ConfirmDelivered = true) is strictly required.");
        }

        var deliveryRef = request.DeliveryReference?.Trim();
        if (string.IsNullOrWhiteSpace(deliveryRef) || deliveryRef.Length < 3 || deliveryRef.Length > 100)
        {
            return ManualFulfillmentOperationResult.BadRequest("A valid external delivery reference or support ticket identifier between 3 and 100 characters is required.");
        }

        if (deliveryRef.Contains('<') || deliveryRef.Contains('>'))
        {
            return ManualFulfillmentOperationResult.BadRequest("DeliveryReference contains invalid characters.");
        }

        var deliveryChannel = request.DeliveryChannel?.Trim();
        if (string.IsNullOrWhiteSpace(deliveryChannel) || deliveryChannel.Length < 2 || deliveryChannel.Length > 50)
        {
            return ManualFulfillmentOperationResult.BadRequest("A valid non-sensitive delivery channel identifier between 2 and 50 characters is required.");
        }

        if (deliveryChannel.Contains('<') || deliveryChannel.Contains('>'))
        {
            return ManualFulfillmentOperationResult.BadRequest("DeliveryChannel contains invalid characters.");
        }

        var notes = request.Notes?.Trim();
        if (notes != null && (notes.Contains('<') || notes.Contains('>')))
        {
            return ManualFulfillmentOperationResult.BadRequest("Notes contain invalid characters.");
        }

        await using var tx = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            : null;

        try
        {
            // 3. Lock Order row
            Order? order;
            if (_db.Database.IsRelational())
            {
                order = await _db.Orders
                    .FromSqlInterpolated($"SELECT * FROM \"Orders\" WHERE \"OrderNumber\" = {orderNumber.Trim()} FOR UPDATE")
                    .FirstOrDefaultAsync(cancellationToken);
            }
            else
            {
                order = await _db.Orders
                    .FirstOrDefaultAsync(o => o.OrderNumber == orderNumber.Trim(), cancellationToken);
            }

            if (order == null)
            {
                if (tx != null) await tx.RollbackAsync(cancellationToken);
                return ManualFulfillmentOperationResult.NotFound($"Order '{orderNumber}' was not found.");
            }

            if (order.Status != OrderStatus.Processing)
            {
                if (tx != null) await tx.RollbackAsync(cancellationToken);
                if (order.Status == OrderStatus.Completed)
                {
                    return ManualFulfillmentOperationResult.Conflict($"Order '{orderNumber}' is already Completed.");
                }
                return ManualFulfillmentOperationResult.Conflict($"Cannot complete fulfillment for order '{orderNumber}' in '{order.Status}' status. Order must be in Processing status.");
            }

            // 4. Verify trusted Paid OrderPurchase payment remains valid
            var paidPayment = await _db.Payments
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.OrderId == order.Id &&
                                          p.Purpose == PaymentPurpose.OrderPurchase &&
                                          p.Status == PaymentStatus.Paid, cancellationToken);

            if (paidPayment == null || paidPayment.Amount != order.TotalAmount || paidPayment.Currency != order.Currency)
            {
                if (tx != null) await tx.RollbackAsync(cancellationToken);
                return ManualFulfillmentOperationResult.BadRequest($"Verified payment validation failed for order '{orderNumber}'.");
            }

            // 5. Verify purchased OrderItem belongs to the Order
            var orderItem = await _db.OrderItems
                .FirstOrDefaultAsync(oi => oi.Id == orderItemId && oi.OrderId == order.Id, cancellationToken);

            if (orderItem == null)
            {
                if (tx != null) await tx.RollbackAsync(cancellationToken);
                return ManualFulfillmentOperationResult.NotFound($"Order item {orderItemId} does not belong to order '{orderNumber}'.");
            }

            // 6. Query Fulfillment with attempts
            var fulfillment = await _db.Fulfillments
                .Include(f => f.Attempts)
                .FirstOrDefaultAsync(f => f.OrderItemId == orderItemId, cancellationToken);

            if (fulfillment == null)
            {
                if (tx != null) await tx.RollbackAsync(cancellationToken);
                return ManualFulfillmentOperationResult.NotFound($"Fulfillment record not found for order item {orderItemId}. Manual fulfillment must be started before it can be completed.");
            }

            if (fulfillment.Status == FulfillmentStatus.Completed)
            {
                if (tx != null) await tx.RollbackAsync(cancellationToken);
                return ManualFulfillmentOperationResult.Conflict($"Fulfillment for order item {orderItemId} is already Completed.");
            }

            if (fulfillment.Status != FulfillmentStatus.InProgress)
            {
                if (tx != null) await tx.RollbackAsync(cancellationToken);
                return ManualFulfillmentOperationResult.Conflict($"Cannot complete fulfillment in '{fulfillment.Status}' status. Fulfillment must be InProgress.");
            }

            // 7. Verify no unresolved vendor attempts exist
            if (fulfillment.Attempts.Any(a => a.Status == FulfillmentAttemptStatus.Unknown || a.Status == FulfillmentAttemptStatus.RequiresReconciliation))
            {
                if (tx != null) await tx.RollbackAsync(cancellationToken);
                return ManualFulfillmentOperationResult.Conflict("Cannot complete manual fulfillment: A previous supplier attempt has an unconfirmed status (Unknown or RequiresReconciliation). Status reconciliation is strictly required before completion.");
            }

            // 8. Find active manual attempt
            var activeAttempt = fulfillment.Attempts.OrderByDescending(a => a.AttemptNumber).FirstOrDefault();
            if (activeAttempt == null || activeAttempt.VendorProductId != null || activeAttempt.Status != FulfillmentAttemptStatus.Submitted)
            {
                if (tx != null) await tx.RollbackAsync(cancellationToken);
                return ManualFulfillmentOperationResult.BadRequest("No active manual fulfillment attempt in Submitted status was found for this line item.");
            }

            // 9. Update the manual attempt to Succeeded
            var now = DateTime.UtcNow;
            activeAttempt.Status = FulfillmentAttemptStatus.Succeeded;
            activeAttempt.ConfirmedByUserId = adminUserId;
            activeAttempt.DeliveryReference = deliveryRef;
            activeAttempt.DeliveryChannel = deliveryChannel;
            activeAttempt.Notes = notes;
            activeAttempt.CompletedAt = now;
            activeAttempt.UpdatedAt = now;

            // 10. Update Fulfillment to Completed & set QuantityFulfilled
            var previousFulfillmentStatus = fulfillment.Status;
            fulfillment.QuantityFulfilled = fulfillment.QuantityRequested;
            fulfillment.Status = FulfillmentStatus.Completed;
            fulfillment.CompletedAt = now;
            fulfillment.UpdatedAt = now;

            // 11. Record FulfillmentStatusHistory
            var statusHistory = new FulfillmentStatusHistory
            {
                FulfillmentId = fulfillment.Id,
                PreviousStatus = previousFulfillmentStatus,
                NewStatus = FulfillmentStatus.Completed,
                Reason = $"Manual delivery confirmed via {deliveryChannel}. Ref: {deliveryRef}",
                ChangedByUserId = adminUserId,
                ChangedAt = now
            };
            _db.FulfillmentStatusHistories.Add(statusHistory);

            // 12. Order Completion Logic (Section 9):
            // Check every purchased item on this order
            var allItems = await _db.OrderItems
                .AsNoTracking()
                .Where(oi => oi.OrderId == order.Id)
                .ToListAsync(cancellationToken);

            var allItemIds = allItems.Select(oi => oi.Id).ToList();

            var allFulfillments = await _db.Fulfillments
                .Where(f => allItemIds.Contains(f.OrderItemId))
                .ToListAsync(cancellationToken);

            bool allItemsCompleted = allItems.All(item =>
            {
                if (item.Id == orderItemId) return true; // currently being marked Completed
                var otherFulfillment = allFulfillments.FirstOrDefault(f => f.OrderItemId == item.Id);
                return otherFulfillment != null && otherFulfillment.Status == FulfillmentStatus.Completed;
            });

            if (allItemsCompleted)
            {
                var previousOrderStatus = order.Status;
                order.Status = OrderStatus.Completed;
                order.UpdatedAt = now;

                var orderHistory = new OrderStatusHistory
                {
                    OrderId = order.Id,
                    PreviousStatus = previousOrderStatus,
                    NewStatus = OrderStatus.Completed,
                    Reason = "All line items successfully fulfilled via manual delivery. Order completed.",
                    ChangedByUserId = adminUserId,
                    ChangedAt = now
                };
                _db.OrderStatusHistories.Add(orderHistory);

                _logger.LogInformation("Order {OrderNumber} marked Completed: all {ItemCount} line items fulfilled.", orderNumber, allItems.Count);
            }
            else
            {
                _logger.LogInformation("Order {OrderNumber} remains in Processing status: not all line items are fulfilled yet.", orderNumber);
            }

            await _db.SaveChangesAsync(cancellationToken);
            if (tx != null) await tx.CommitAsync(cancellationToken);

            _logger.LogInformation("Administrator {AdminUserId} completed manual fulfillment for Order {OrderNumber}, Item {OrderItemId}, Fulfillment {FulfillmentId}.",
                adminUserId, orderNumber, orderItemId, fulfillment.Id);

            var dto = await BuildAdminFulfillmentDtoAsync(fulfillment.Id, cancellationToken);
            return ManualFulfillmentOperationResult.Success(dto!, "Manual fulfillment completed successfully.");
        }
        catch (Exception ex)
        {
            if (tx != null) await tx.RollbackAsync(cancellationToken);
            _logger.LogError(ex, "Unexpected error completing manual fulfillment for Order {OrderNumber}, Item {OrderItemId}.", orderNumber, orderItemId);
            return ManualFulfillmentOperationResult.BadRequest("An error occurred while completing manual fulfillment. Operation rolled back safely.");
        }
    }

    /// <inheritdoc />
    public async Task<AdminOrderFulfillmentsDto?> GetOrderFulfillmentsAsync(string orderNumber, CancellationToken cancellationToken = default)
    {
        var order = await _db.Orders
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.OrderNumber == orderNumber.Trim(), cancellationToken);

        if (order == null)
        {
            return null;
        }

        var fulfillments = await _db.Fulfillments
            .AsNoTracking()
            .Include(f => f.OrderItem)
            .Include(f => f.Attempts)
                .ThenInclude(a => a.VendorProduct)
                    .ThenInclude(vp => vp!.Vendor)
            .Include(f => f.StatusHistory)
            .Where(f => f.OrderId == order.Id)
            .OrderBy(f => f.OrderItemId)
            .ToListAsync(cancellationToken);

        var fulfillmentDtos = fulfillments.Select(f => new AdminFulfillmentDto
        {
            FulfillmentId = f.Id,
            OrderId = f.OrderId,
            OrderItemId = f.OrderItemId,
            ProductSku = f.OrderItem?.ProductSkuSnapshot ?? string.Empty,
            ProductName = f.OrderItem?.ProductNameSnapshot ?? string.Empty,
            UnitPrice = f.OrderItem?.UnitPriceSnapshot ?? 0m,
            Currency = f.OrderItem?.Currency ?? "PKR",
            QuantityRequested = f.QuantityRequested,
            QuantityFulfilled = f.QuantityFulfilled,
            Status = f.Status.ToString(),
            FulfillmentMethod = f.FulfillmentMethod.ToString(),
            CreatedAt = f.CreatedAt,
            UpdatedAt = f.UpdatedAt,
            CompletedAt = f.CompletedAt,
            Attempts = f.Attempts.OrderBy(a => a.AttemptNumber).Select(a => new AdminFulfillmentAttemptDto
            {
                AttemptId = a.Id,
                AttemptNumber = a.AttemptNumber,
                AttemptKey = a.AttemptKey,
                VendorProductId = a.VendorProductId,
                VendorId = a.VendorProduct?.VendorId,
                VendorName = a.VendorProduct?.Vendor?.InternalName,
                Status = a.Status.ToString(),
                ExternalVendorOrderId = a.ExternalVendorOrderId,
                FailureCategory = a.FailureCategory?.ToString(),
                SafeErrorSummary = a.SafeErrorSummary,
                DeliveryReference = a.DeliveryReference,
                DeliveryChannel = a.DeliveryChannel,
                Notes = a.Notes,
                InitiatedByUserId = a.InitiatedByUserId,
                ConfirmedByUserId = a.ConfirmedByUserId,
                CreatedAt = a.CreatedAt,
                SubmittedAt = a.SubmittedAt,
                CompletedAt = a.CompletedAt
            }).ToList(),
            StatusHistory = f.StatusHistory.OrderBy(h => h.ChangedAt).Select(h => new AdminFulfillmentStatusHistoryDto
            {
                Id = h.Id,
                FulfillmentId = h.FulfillmentId,
                PreviousStatus = h.PreviousStatus?.ToString(),
                NewStatus = h.NewStatus.ToString(),
                Reason = h.Reason,
                ChangedByUserId = h.ChangedByUserId,
                ChangedAt = h.ChangedAt
            }).ToList()
        }).ToList();

        return new AdminOrderFulfillmentsDto
        {
            OrderNumber = order.OrderNumber,
            OrderId = order.Id,
            OrderStatus = order.Status.ToString(),
            Fulfillments = fulfillmentDtos
        };
    }

    private async Task<AdminFulfillmentDto?> BuildAdminFulfillmentDtoAsync(long fulfillmentId, CancellationToken cancellationToken)
    {
        var f = await _db.Fulfillments
            .AsNoTracking()
            .Include(f => f.OrderItem)
            .Include(f => f.Attempts)
                .ThenInclude(a => a.VendorProduct)
                    .ThenInclude(vp => vp!.Vendor)
            .Include(f => f.StatusHistory)
            .FirstOrDefaultAsync(f => f.Id == fulfillmentId, cancellationToken);

        if (f == null) return null;

        return new AdminFulfillmentDto
        {
            FulfillmentId = f.Id,
            OrderId = f.OrderId,
            OrderItemId = f.OrderItemId,
            ProductSku = f.OrderItem?.ProductSkuSnapshot ?? string.Empty,
            ProductName = f.OrderItem?.ProductNameSnapshot ?? string.Empty,
            UnitPrice = f.OrderItem?.UnitPriceSnapshot ?? 0m,
            Currency = f.OrderItem?.Currency ?? "PKR",
            QuantityRequested = f.QuantityRequested,
            QuantityFulfilled = f.QuantityFulfilled,
            Status = f.Status.ToString(),
            FulfillmentMethod = f.FulfillmentMethod.ToString(),
            CreatedAt = f.CreatedAt,
            UpdatedAt = f.UpdatedAt,
            CompletedAt = f.CompletedAt,
            Attempts = f.Attempts.OrderBy(a => a.AttemptNumber).Select(a => new AdminFulfillmentAttemptDto
            {
                AttemptId = a.Id,
                AttemptNumber = a.AttemptNumber,
                AttemptKey = a.AttemptKey,
                VendorProductId = a.VendorProductId,
                VendorId = a.VendorProduct?.VendorId,
                VendorName = a.VendorProduct?.Vendor?.InternalName,
                Status = a.Status.ToString(),
                ExternalVendorOrderId = a.ExternalVendorOrderId,
                FailureCategory = a.FailureCategory?.ToString(),
                SafeErrorSummary = a.SafeErrorSummary,
                DeliveryReference = a.DeliveryReference,
                DeliveryChannel = a.DeliveryChannel,
                Notes = a.Notes,
                InitiatedByUserId = a.InitiatedByUserId,
                ConfirmedByUserId = a.ConfirmedByUserId,
                CreatedAt = a.CreatedAt,
                SubmittedAt = a.SubmittedAt,
                CompletedAt = a.CompletedAt
            }).ToList(),
            StatusHistory = f.StatusHistory.OrderBy(h => h.ChangedAt).Select(h => new AdminFulfillmentStatusHistoryDto
            {
                Id = h.Id,
                FulfillmentId = h.FulfillmentId,
                PreviousStatus = h.PreviousStatus?.ToString(),
                NewStatus = h.NewStatus.ToString(),
                Reason = h.Reason,
                ChangedByUserId = h.ChangedByUserId,
                ChangedAt = h.ChangedAt
            }).ToList()
        };
    }

    /// <inheritdoc />
    public async Task<PagedResult<AdminFulfillmentSummaryDto>> GetFulfillmentsAsync(
        AdminFulfillmentQuery query,
        CancellationToken cancellationToken = default)
    {
        var baseQuery = _db.OrderItems
            .AsNoTracking()
            .Include(oi => oi.Order)
                .ThenInclude(o => o.Customer)
            .Include(oi => oi.Fulfillment)
                .ThenInclude(f => f!.Attempts)
            .AsQueryable();

        // 1. Order status filtering & default eligibility rules
        if (!string.IsNullOrWhiteSpace(query.OrderStatus) &&
            Enum.TryParse<OrderStatus>(query.OrderStatus.Trim(), true, out var filterOrderStatus))
        {
            baseQuery = baseQuery.Where(oi => oi.Order.Status == filterOrderStatus);
        }
        else
        {
            // Default queue eligibility: include items that have a tracked fulfillment record,
            // OR items on actionable/paid orders (excluding unpaid checkout drafts, cancelled, and checkout-failed orders without fulfillment).
            baseQuery = baseQuery.Where(oi => oi.Fulfillment != null ||
                (oi.Order.Status != OrderStatus.PendingPayment &&
                 oi.Order.Status != OrderStatus.Cancelled &&
                 oi.Order.Status != OrderStatus.Failed));
        }

        // 2. Queue / Fulfillment status filter
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            var statusStr = query.Status.Trim();
            if (statusStr.Equals("Uninitialized", StringComparison.OrdinalIgnoreCase))
            {
                baseQuery = baseQuery.Where(oi => oi.Fulfillment == null);
            }
            else if (Enum.TryParse<FulfillmentStatus>(statusStr, true, out var filterFulfillmentStatus))
            {
                baseQuery = baseQuery.Where(oi => oi.Fulfillment != null && oi.Fulfillment.Status == filterFulfillmentStatus);
            }
        }

        // 3. Fulfillment method filter
        if (!string.IsNullOrWhiteSpace(query.FulfillmentMethod) &&
            Enum.TryParse<FulfillmentMethod>(query.FulfillmentMethod.Trim(), true, out var filterMethod))
        {
            if (filterMethod == FulfillmentMethod.Unassigned)
            {
                baseQuery = baseQuery.Where(oi => oi.Fulfillment == null || oi.Fulfillment.FulfillmentMethod == FulfillmentMethod.Unassigned);
            }
            else
            {
                baseQuery = baseQuery.Where(oi => oi.Fulfillment != null && oi.Fulfillment.FulfillmentMethod == filterMethod);
            }
        }

        // 4. Multi-field search
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var cleanSearch = query.Search.Trim().ToLower();
            baseQuery = baseQuery.Where(oi =>
                oi.Order.OrderNumber.ToLower().Contains(cleanSearch) ||
                oi.ProductSkuSnapshot.ToLower().Contains(cleanSearch) ||
                oi.ProductNameSnapshot.ToLower().Contains(cleanSearch) ||
                (oi.Order.Customer != null && (
                    (oi.Order.Customer.Email != null && oi.Order.Customer.Email.ToLower().Contains(cleanSearch)) ||
                    (oi.Order.Customer.FullName != null && oi.Order.Customer.FullName.ToLower().Contains(cleanSearch)) ||
                    oi.Order.CustomerId.ToLower().Contains(cleanSearch)
                ))
            );
        }

        // 5. Total count for server-side pagination
        var totalCount = await baseQuery.CountAsync(cancellationToken);

        // 6. Deterministic pagination & ordering (newest orders first, unique OrderItemId tie-breaker)
        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize < 1 ? 20 : (query.PageSize > 100 ? 100 : query.PageSize);

        var items = await baseQuery
            .OrderByDescending(oi => oi.Order.CreatedAt)
            .ThenByDescending(oi => oi.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        // 7. Project to safe AdminFulfillmentSummaryDto
        var dtos = items.Select(oi =>
        {
            var f = oi.Fulfillment;
            var isInitialized = f != null;
            var fulfillmentStatus = f != null ? f.Status.ToString() : null;
            var queueStatus = f != null ? f.Status.ToString() : "Uninitialized";
            var method = f != null ? f.FulfillmentMethod.ToString() : FulfillmentMethod.Unassigned.ToString();
            var quantityFulfilled = f != null ? f.QuantityFulfilled : 0;
            var attemptCount = f != null ? f.Attempts.Count : 0;
            var createdAt = f != null ? f.CreatedAt : oi.Order.CreatedAt;
            var updatedAt = f != null ? f.UpdatedAt : oi.Order.UpdatedAt;
            var completedAt = f?.CompletedAt;

            return new AdminFulfillmentSummaryDto
            {
                FulfillmentId = f?.Id,
                IsInitialized = isInitialized,
                OrderId = oi.OrderId,
                OrderNumber = oi.Order.OrderNumber,
                OrderItemId = oi.Id,
                CustomerId = oi.Order.CustomerId,
                CustomerEmail = oi.Order.Customer?.Email,
                CustomerName = oi.Order.Customer?.FullName,
                ProductSku = oi.ProductSkuSnapshot,
                ProductName = oi.ProductNameSnapshot,
                QuantityRequested = oi.Quantity,
                QuantityFulfilled = quantityFulfilled,
                FulfillmentMethod = method,
                FulfillmentStatus = fulfillmentStatus,
                QueueStatus = queueStatus,
                OrderStatus = oi.Order.Status.ToString(),
                AttemptCount = attemptCount,
                CreatedAt = createdAt,
                UpdatedAt = updatedAt,
                CompletedAt = completedAt
            };
        }).ToList();

        return new PagedResult<AdminFulfillmentSummaryDto>(dtos, page, pageSize, totalCount);
    }
}
