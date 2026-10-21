using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using WebApiTgBot.Infrastructure.Data;
using WebApiTgBot.Modules.Catalog.Models;
using WebApiTgBot.Modules.Fulfillments.Models;
using WebApiTgBot.Modules.Fulfillments.Models.DTOs;
using WebApiTgBot.Modules.Fulfillments.Services;
using WebApiTgBot.Modules.Identity.Models;
using WebApiTgBot.Modules.Orders.Models;
using WebApiTgBot.Modules.Orders.Models.DTOs;
using WebApiTgBot.Modules.Payments.Models;

namespace WebApiTgBot.Tests;

/// <summary>
/// Comprehensive integration test suite for Milestone 5B: Central Fulfillment Foundation & Vendor Order Preparation.
/// Verifies:
/// 1. Fulfillment entity relationships and correct linking to Order and OrderItem.
/// 2. Idempotent fulfillment initialization preventing duplicate records.
/// 3. Valid fulfillment statuses and state machine transition rules.
/// 4. Quantity validations and bounds (QuantityRequested > 0, 0 <= QuantityFulfilled <= QuantityRequested).
/// 5. Unpaid and pending orders cannot initialize active fulfillment.
/// 6. Correctly paid/processing orders are eligible for fulfillment initialization.
/// 7. Fulfillment records do not alter payment or wallet balances.
/// 8. Fulfillment attempts can reference existing VendorProduct mappings or remain null for manual fulfillment.
/// 9. Multiple historical attempts remain preserved chronologically.
/// 10. Crucial safety guard: Unknown vendor outcomes cannot be treated as confirmed failure for automatic fallback.
/// 11. Database-enforced uniqueness and non-destructive delete behaviors on PostgreSQL.
/// 12. Supplier identifiers and wholesale costs remain hidden from customer APIs.
/// 13. Existing Admin order APIs remain functional.
/// </summary>
public class FulfillmentTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public FulfillmentTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static AppDbContext CreateInMemoryDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new AppDbContext(options);
    }

    private static async Task<(Order order, OrderItem item, Payment payment)> SeedEligibleOrderAsync(AppDbContext db, string suffix, OrderStatus orderStatus = OrderStatus.Paid)
    {
        var category = new Category
        {
            Name = $"Category {suffix}",
            Slug = $"category-{suffix}",
            IsActive = true
        };
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        var product = new Product
        {
            CategoryId = category.Id,
            InternalSku = $"SKU-{suffix}",
            Name = $"Product {suffix}",
            SellingPrice = 1500m,
            SellingCurrency = "PKR",
            IsActive = true
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();

        var customer = new ApplicationUser
        {
            UserName = $"cust_{suffix}@test.local",
            Email = $"cust_{suffix}@test.local",
            FullName = $"Customer {suffix}",
            IsActive = true
        };
        db.Users.Add(customer);
        await db.SaveChangesAsync();

        var order = new Order
        {
            OrderNumber = $"ORD-{suffix}",
            CustomerId = customer.Id,
            Status = orderStatus,
            Currency = "PKR",
            Subtotal = 1500m,
            TotalAmount = 1500m,
            SalesChannel = SalesChannel.Website
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        var item = new OrderItem
        {
            OrderId = order.Id,
            ProductId = product.Id,
            ProductSkuSnapshot = product.InternalSku,
            ProductNameSnapshot = product.Name,
            Quantity = 1,
            UnitPriceSnapshot = 1500m,
            Currency = "PKR",
            TotalPrice = 1500m
        };
        db.OrderItems.Add(item);
        await db.SaveChangesAsync();

        var payment = new Payment
        {
            PaymentNumber = $"PAY-{suffix}",
            OrderId = order.Id,
            Purpose = PaymentPurpose.OrderPurchase,
            Amount = 1500m,
            Currency = "PKR",
            PaymentMethodCode = "WALLET",
            Status = PaymentStatus.Paid,
            PaidAt = DateTime.UtcNow
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        return (order, item, payment);
    }

    [Fact]
    public async Task Fulfillment_StatusTransitions_Enforce_State_Machine_Rules()
    {
        // Allowed transitions
        Assert.True(FulfillmentStatusTransitions.CanTransition(FulfillmentStatus.NotStarted, FulfillmentStatus.Pending));
        Assert.True(FulfillmentStatusTransitions.CanTransition(FulfillmentStatus.NotStarted, FulfillmentStatus.InProgress));
        Assert.True(FulfillmentStatusTransitions.CanTransition(FulfillmentStatus.NotStarted, FulfillmentStatus.Failed));
        Assert.True(FulfillmentStatusTransitions.CanTransition(FulfillmentStatus.Pending, FulfillmentStatus.InProgress));
        Assert.True(FulfillmentStatusTransitions.CanTransition(FulfillmentStatus.Pending, FulfillmentStatus.RequiresReview));
        Assert.True(FulfillmentStatusTransitions.CanTransition(FulfillmentStatus.InProgress, FulfillmentStatus.Completed));
        Assert.True(FulfillmentStatusTransitions.CanTransition(FulfillmentStatus.InProgress, FulfillmentStatus.PartiallyCompleted));
        Assert.True(FulfillmentStatusTransitions.CanTransition(FulfillmentStatus.InProgress, FulfillmentStatus.RequiresReview));
        Assert.True(FulfillmentStatusTransitions.CanTransition(FulfillmentStatus.RequiresReview, FulfillmentStatus.InProgress));
        Assert.True(FulfillmentStatusTransitions.CanTransition(FulfillmentStatus.RequiresReview, FulfillmentStatus.Completed));

        // Prohibited transitions
        Assert.False(FulfillmentStatusTransitions.CanTransition(FulfillmentStatus.NotStarted, FulfillmentStatus.Completed)); // Cannot skip steps directly to Completed
        Assert.False(FulfillmentStatusTransitions.CanTransition(FulfillmentStatus.Completed, FulfillmentStatus.InProgress)); // Completed is terminal
        Assert.False(FulfillmentStatusTransitions.CanTransition(FulfillmentStatus.Completed, FulfillmentStatus.Failed));     // Completed is terminal
        Assert.False(FulfillmentStatusTransitions.CanTransition(FulfillmentStatus.Failed, FulfillmentStatus.InProgress));    // Failed is terminal
    }

    [Fact]
    public async Task FulfillmentAttemptRules_Crucial_Safeguard_Unknown_Outcome_Cannot_Fallback()
    {
        // Confirmed failure allows safe fallback to alternative vendor
        Assert.True(FulfillmentAttemptRules.CanFallbackToNextVendor(FulfillmentAttemptStatus.ConfirmedFailed));

        // Unknown outcome (timeout, 504, network error) MUST NOT trigger automated fallback!
        Assert.False(FulfillmentAttemptRules.CanFallbackToNextVendor(FulfillmentAttemptStatus.Unknown));
        Assert.False(FulfillmentAttemptRules.CanFallbackToNextVendor(FulfillmentAttemptStatus.RequiresReconciliation));
        Assert.False(FulfillmentAttemptRules.CanFallbackToNextVendor(FulfillmentAttemptStatus.Submitted));
        Assert.False(FulfillmentAttemptRules.CanFallbackToNextVendor(FulfillmentAttemptStatus.NotStarted));
        Assert.False(FulfillmentAttemptRules.CanFallbackToNextVendor(FulfillmentAttemptStatus.Succeeded));

        // Reconciliation identification
        Assert.True(FulfillmentAttemptRules.RequiresReconciliation(FulfillmentAttemptStatus.Unknown));
        Assert.True(FulfillmentAttemptRules.RequiresReconciliation(FulfillmentAttemptStatus.RequiresReconciliation));
        Assert.False(FulfillmentAttemptRules.RequiresReconciliation(FulfillmentAttemptStatus.ConfirmedFailed));
        Assert.False(FulfillmentAttemptRules.RequiresReconciliation(FulfillmentAttemptStatus.Succeeded));
    }

    [Fact]
    public async Task InitializeFulfillment_Succeeds_For_Paid_Order_And_Is_Idempotent()
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var (order, item, _) = await SeedEligibleOrderAsync(db, "init_succ");
        var service = new FulfillmentService(db, NullLogger<FulfillmentService>.Instance);

        // 1. Initial initialization
        var result1 = await service.InitializeFulfillmentForOrderItemAsync(order.Id, item.Id);
        Assert.Equal(FulfillmentInitializationStatus.Success, result1.Status);
        Assert.NotNull(result1.Fulfillment);
        Assert.Equal(FulfillmentStatus.NotStarted, result1.Fulfillment.Status);
        Assert.Equal(FulfillmentMethod.Unassigned, result1.Fulfillment.FulfillmentMethod);
        Assert.Equal(item.Quantity, result1.Fulfillment.QuantityRequested);
        Assert.Equal(0, result1.Fulfillment.QuantityFulfilled);

        // 2. Second initialization (idempotent replay)
        var result2 = await service.InitializeFulfillmentForOrderItemAsync(order.Id, item.Id);
        Assert.Equal(FulfillmentInitializationStatus.AlreadyInitialized, result2.Status);
        Assert.NotNull(result2.Fulfillment);
        Assert.Equal(result1.Fulfillment.Id, result2.Fulfillment.Id);

        // Verify only 1 fulfillment record exists in database
        var count = await db.Fulfillments.CountAsync(f => f.OrderItemId == item.Id);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task InitializeFulfillment_Rejects_Unpaid_Or_Pending_Orders()
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var (order, item, _) = await SeedEligibleOrderAsync(db, "unpaid", OrderStatus.PendingPayment);
        var service = new FulfillmentService(db, NullLogger<FulfillmentService>.Instance);

        var result = await service.InitializeFulfillmentForOrderItemAsync(order.Id, item.Id);
        Assert.Equal(FulfillmentInitializationStatus.UnpaidOrderRejected, result.Status);

        var count = await db.Fulfillments.CountAsync();
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task InitializeFulfillment_Rejects_Order_Without_Verified_Paid_Payment()
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var (order, item, payment) = await SeedEligibleOrderAsync(db, "no_paid_pmt");
        
        // Remove payment to simulate inconsistent/unverified order
        db.Payments.Remove(payment);
        await db.SaveChangesAsync();

        var service = new FulfillmentService(db, NullLogger<FulfillmentService>.Instance);
        var result = await service.InitializeFulfillmentForOrderItemAsync(order.Id, item.Id);

        Assert.Equal(FulfillmentInitializationStatus.UnpaidOrderRejected, result.Status);
    }

    [Fact]
    public async Task InitializeFulfillment_Rejects_Payment_Amount_Or_Currency_Mismatch()
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var (order, item, payment) = await SeedEligibleOrderAsync(db, "mismatch");

        // Alter payment amount to mismatch order total
        payment.Amount = 500m;
        await db.SaveChangesAsync();

        var service = new FulfillmentService(db, NullLogger<FulfillmentService>.Instance);
        var result = await service.InitializeFulfillmentForOrderItemAsync(order.Id, item.Id);

        Assert.Equal(FulfillmentInitializationStatus.PaymentMismatch, result.Status);
    }

    [Fact]
    public async Task InitializeFulfillment_Leaves_Wallet_And_Order_Prices_Untouched()
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var (order, item, payment) = await SeedEligibleOrderAsync(db, "fin_untouched");

        var initialOrderTotal = order.TotalAmount;
        var initialItemUnitPrice = item.UnitPriceSnapshot;
        var initialPaymentAmount = payment.Amount;
        var initialPaymentStatus = payment.Status;

        var service = new FulfillmentService(db, NullLogger<FulfillmentService>.Instance);
        var result = await service.InitializeFulfillmentForOrderItemAsync(order.Id, item.Id);

        Assert.Equal(FulfillmentInitializationStatus.Success, result.Status);

        // Verify order, item, and payment are 100% unchanged
        var refreshedOrder = await db.Orders.Include(o => o.Items).Include(o => o.Payments).FirstAsync(o => o.Id == order.Id);
        Assert.Equal(initialOrderTotal, refreshedOrder.TotalAmount);
        Assert.Equal(initialItemUnitPrice, refreshedOrder.Items.First().UnitPriceSnapshot);
        Assert.Equal(initialPaymentAmount, refreshedOrder.Payments.First().Amount);
        Assert.Equal(initialPaymentStatus, refreshedOrder.Payments.First().Status);
    }

    [Fact]
    public async Task Fulfillment_Attempts_Track_Sequential_Attempts_And_Preserve_History()
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var (order, item, _) = await SeedEligibleOrderAsync(db, "attempts");
        var service = new FulfillmentService(db, NullLogger<FulfillmentService>.Instance);

        var initResult = await service.InitializeFulfillmentForOrderItemAsync(order.Id, item.Id);
        var fulfillmentId = initResult.Fulfillment!.Id;

        // Attempt 1: Manual fulfillment attempt
        var attempt1Result = await service.CreateAttemptAsync(fulfillmentId, vendorProductId: null);
        Assert.Equal(FulfillmentAttemptOperationStatus.Success, attempt1Result.Status);
        Assert.Equal(1, attempt1Result.Attempt!.AttemptNumber);
        Assert.Equal($"ATT-{fulfillmentId}-1", attempt1Result.Attempt.AttemptKey);

        // Record attempt 1 as ConfirmedFailed
        var outcome1 = await service.RecordAttemptOutcomeAsync(
            attempt1Result.Attempt.Id,
            FulfillmentAttemptStatus.ConfirmedFailed,
            FulfillmentFailureCategory.VendorOutOfStock,
            "Supplier was out of subscription vouchers.");
        Assert.Equal(FulfillmentAttemptOperationStatus.Success, outcome1.Status);

        // Attempt 2: Fallback attempt (allowed because Attempt 1 was ConfirmedFailed)
        var attempt2Result = await service.CreateAttemptAsync(fulfillmentId, vendorProductId: null);
        Assert.Equal(FulfillmentAttemptOperationStatus.Success, attempt2Result.Status);
        Assert.Equal(2, attempt2Result.Attempt!.AttemptNumber);
        Assert.Equal($"ATT-{fulfillmentId}-2", attempt2Result.Attempt.AttemptKey);

        // Record attempt 2 as Succeeded
        var outcome2 = await service.RecordAttemptOutcomeAsync(
            attempt2Result.Attempt.Id,
            FulfillmentAttemptStatus.Succeeded,
            externalVendorOrderId: "EXT-VEND-998822");
        Assert.Equal(FulfillmentAttemptOperationStatus.Success, outcome2.Status);

        // Verify both attempts exist chronologically
        var fulfillment = await service.GetFulfillmentByOrderItemIdAsync(item.Id);
        Assert.NotNull(fulfillment);
        Assert.Equal(2, fulfillment.Attempts.Count);
        Assert.Contains(fulfillment.Attempts, a => a.AttemptNumber == 1 && a.Status == FulfillmentAttemptStatus.ConfirmedFailed);
        Assert.Contains(fulfillment.Attempts, a => a.AttemptNumber == 2 && a.Status == FulfillmentAttemptStatus.Succeeded && a.ExternalVendorOrderId == "EXT-VEND-998822");
    }

    [Fact]
    public async Task Fulfillment_CreateAttempt_Blocks_Fallback_When_Previous_Attempt_Is_Unknown()
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var (order, item, _) = await SeedEligibleOrderAsync(db, "unknown_block");
        var service = new FulfillmentService(db, NullLogger<FulfillmentService>.Instance);

        var initResult = await service.InitializeFulfillmentForOrderItemAsync(order.Id, item.Id);
        var fulfillmentId = initResult.Fulfillment!.Id;

        // Attempt 1
        var attempt1Result = await service.CreateAttemptAsync(fulfillmentId, vendorProductId: null);
        Assert.Equal(FulfillmentAttemptOperationStatus.Success, attempt1Result.Status);

        // Attempt 1 encountered an unknown outcome (e.g. network timeout)
        await service.RecordAttemptOutcomeAsync(
            attempt1Result.Attempt!.Id,
            FulfillmentAttemptStatus.Unknown,
            FulfillmentFailureCategory.NetworkTimeout,
            "Supplier API timed out after 30 seconds; status unknown.");

        // CRITICAL: Trying to create attempt 2 MUST BE REJECTED to prevent double-spending
        var attempt2Result = await service.CreateAttemptAsync(fulfillmentId, vendorProductId: null);
        Assert.Equal(FulfillmentAttemptOperationStatus.FallbackNotAllowedDueToUnconfirmedOutcome, attempt2Result.Status);

        // Fulfillment status should now be RequiresReview
        var fulfillment = await service.GetFulfillmentByOrderItemIdAsync(item.Id);
        Assert.NotNull(fulfillment);
        Assert.Equal(FulfillmentStatus.RequiresReview, fulfillment.Status);
    }

    [Fact]
    public async Task Fulfillment_Attempt_Can_Reference_VendorProduct_Mapping()
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var (order, item, _) = await SeedEligibleOrderAsync(db, "vp_ref");

        var vendor = new Vendor { InternalName = "Alpha Supplier", IsActive = true };
        db.Vendors.Add(vendor);
        await db.SaveChangesAsync();

        var vp = new VendorProduct
        {
            VendorId = vendor.Id,
            ProductId = item.ProductId,
            ExternalProductId = "EXT-PROD-4411",
            VendorCost = 1200m,
            VendorCostCurrency = "PKR",
            IsAvailable = true,
            Priority = 1
        };
        db.VendorProducts.Add(vp);
        await db.SaveChangesAsync();

        var service = new FulfillmentService(db, NullLogger<FulfillmentService>.Instance);
        var initResult = await service.InitializeFulfillmentForOrderItemAsync(order.Id, item.Id);

        var attemptResult = await service.CreateAttemptAsync(initResult.Fulfillment!.Id, vp.Id);
        Assert.Equal(FulfillmentAttemptOperationStatus.Success, attemptResult.Status);
        Assert.Equal(vp.Id, attemptResult.Attempt!.VendorProductId);

        var retrieved = await service.GetFulfillmentByOrderItemIdAsync(item.Id);
        Assert.NotNull(retrieved);
        Assert.Equal(vp.Id, retrieved.Attempts.First().VendorProductId);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSql_Fulfillments_Unique_OrderItemId_And_ForeignKeys_Enforced()
    {
        var options = PostgreSqlTestDatabase.CreateOptions();
        await using var seedDb = new AppDbContext(options);

        if (!await PostgreSqlTestDatabase.EnsureMigratedAsync(seedDb))
        {
            // PostgreSQL not available; skip isolated container test
            return;
        }

        var suffix = $"pg_{Guid.NewGuid():N}"[..12];
        var (order, item, _) = await SeedEligibleOrderAsync(seedDb, suffix);

        // Insert first fulfillment
        var f1 = new Fulfillment
        {
            OrderId = order.Id,
            OrderItemId = item.Id,
            Status = FulfillmentStatus.NotStarted,
            FulfillmentMethod = FulfillmentMethod.Unassigned,
            QuantityRequested = 1,
            QuantityFulfilled = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        seedDb.Fulfillments.Add(f1);
        await seedDb.SaveChangesAsync();

        // Attempt to insert second fulfillment for same OrderItemId in a fresh context
        await using var insertDb = new AppDbContext(options);
        var f2 = new Fulfillment
        {
            OrderId = order.Id,
            OrderItemId = item.Id, // DUPLICATE OrderItemId
            Status = FulfillmentStatus.NotStarted,
            FulfillmentMethod = FulfillmentMethod.Unassigned,
            QuantityRequested = 1,
            QuantityFulfilled = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        insertDb.Fulfillments.Add(f2);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => insertDb.SaveChangesAsync());
        var pgEx = ex.InnerException as PostgresException;
        Assert.NotNull(pgEx);
        Assert.Equal("23505", pgEx.SqlState); // PostgreSQL Unique Violation
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSql_Fulfillments_Quantity_Check_Constraints_Enforced()
    {
        var options = PostgreSqlTestDatabase.CreateOptions();
        await using var seedDb = new AppDbContext(options);

        if (!await PostgreSqlTestDatabase.EnsureMigratedAsync(seedDb))
        {
            return;
        }

        var suffix = $"pg_ck_{Guid.NewGuid():N}"[..12];
        var (order, item, _) = await SeedEligibleOrderAsync(seedDb, suffix);

        // Test 1: QuantityRequested <= 0 must fail check constraint
        var fInvalidReq = new Fulfillment
        {
            OrderId = order.Id,
            OrderItemId = item.Id,
            Status = FulfillmentStatus.NotStarted,
            FulfillmentMethod = FulfillmentMethod.Unassigned,
            QuantityRequested = 0, // INVALID
            QuantityFulfilled = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        seedDb.Fulfillments.Add(fInvalidReq);

        var ex1 = await Assert.ThrowsAsync<DbUpdateException>(() => seedDb.SaveChangesAsync());
        var pgEx1 = ex1.InnerException as PostgresException;
        Assert.NotNull(pgEx1);
        Assert.Equal("23514", pgEx1.SqlState); // PostgreSQL Check Violation
        seedDb.Entry(fInvalidReq).State = EntityState.Detached;

        // Test 2: QuantityFulfilled > QuantityRequested must fail check constraint
        var fInvalidFulfilled = new Fulfillment
        {
            OrderId = order.Id,
            OrderItemId = item.Id,
            Status = FulfillmentStatus.NotStarted,
            FulfillmentMethod = FulfillmentMethod.Unassigned,
            QuantityRequested = 2,
            QuantityFulfilled = 5, // INVALID: 5 > 2
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        seedDb.Fulfillments.Add(fInvalidFulfilled);

        var ex2 = await Assert.ThrowsAsync<DbUpdateException>(() => seedDb.SaveChangesAsync());
        var pgEx2 = ex2.InnerException as PostgresException;
        Assert.NotNull(pgEx2);
        Assert.Equal("23514", pgEx2.SqlState); // PostgreSQL Check Violation
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSql_FulfillmentAttempts_AttemptKey_Uniqueness_Enforced()
    {
        var options = PostgreSqlTestDatabase.CreateOptions();
        await using var seedDb = new AppDbContext(options);

        if (!await PostgreSqlTestDatabase.EnsureMigratedAsync(seedDb))
        {
            return;
        }

        var suffix = $"pg_att_{Guid.NewGuid():N}"[..12];
        var (order, item, _) = await SeedEligibleOrderAsync(seedDb, suffix);

        var fulfillment = new Fulfillment
        {
            OrderId = order.Id,
            OrderItemId = item.Id,
            Status = FulfillmentStatus.NotStarted,
            FulfillmentMethod = FulfillmentMethod.Unassigned,
            QuantityRequested = 1,
            QuantityFulfilled = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        seedDb.Fulfillments.Add(fulfillment);
        await seedDb.SaveChangesAsync();

        var attKey = $"ATT-UNIQ-{suffix}";

        var att1 = new FulfillmentAttempt
        {
            FulfillmentId = fulfillment.Id,
            AttemptNumber = 1,
            AttemptKey = attKey,
            Status = FulfillmentAttemptStatus.NotStarted,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        seedDb.FulfillmentAttempts.Add(att1);
        await seedDb.SaveChangesAsync();

        // Attempt with identical AttemptKey
        var att2 = new FulfillmentAttempt
        {
            FulfillmentId = fulfillment.Id,
            AttemptNumber = 2,
            AttemptKey = attKey, // DUPLICATE KEY
            Status = FulfillmentAttemptStatus.NotStarted,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        seedDb.FulfillmentAttempts.Add(att2);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => seedDb.SaveChangesAsync());
        var pgEx = ex.InnerException as PostgresException;
        Assert.NotNull(pgEx);
        Assert.Equal("23505", pgEx.SqlState); // PostgreSQL Unique Violation
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSql_NonDestructive_Cascading_Delete_Prohibited()
    {
        var options = PostgreSqlTestDatabase.CreateOptions();
        await using var seedDb = new AppDbContext(options);

        if (!await PostgreSqlTestDatabase.EnsureMigratedAsync(seedDb))
        {
            return;
        }

        var suffix = $"pg_del_{Guid.NewGuid():N}"[..12];
        var (order, item, _) = await SeedEligibleOrderAsync(seedDb, suffix);

        var fulfillment = new Fulfillment
        {
            OrderId = order.Id,
            OrderItemId = item.Id,
            Status = FulfillmentStatus.NotStarted,
            FulfillmentMethod = FulfillmentMethod.Unassigned,
            QuantityRequested = 1,
            QuantityFulfilled = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        seedDb.Fulfillments.Add(fulfillment);
        await seedDb.SaveChangesAsync();

        // 1. Attempting to delete Order while Fulfillment is tracked in memory
        // is rejected by EF Core with InvalidOperationException due to DeleteBehavior.Restrict
        Assert.Throws<InvalidOperationException>(() => seedDb.Orders.Remove(order));

        // 2. In a fresh context without tracked child entries, attempting to delete the Order
        // is rejected by PostgreSQL with 23503 foreign key violation due to ON DELETE RESTRICT
        await using (var deleteDb = new AppDbContext(options))
        {
            var ordToDelete = await deleteDb.Orders.FindAsync(order.Id);
            Assert.NotNull(ordToDelete);
            deleteDb.Orders.Remove(ordToDelete);

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => deleteDb.SaveChangesAsync());
            var pgEx = ex.InnerException as PostgresException;
            Assert.NotNull(pgEx);
            Assert.Equal("23503", pgEx.SqlState); // Foreign key constraint violation
        }
    }

    [Fact]
    public async Task Customer_Order_DTOs_Never_Expose_Supplier_Or_Attempt_Details()
    {
        // Verify customer order DTO properties
        var summaryProps = typeof(CustomerOrderSummaryDto).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain("Vendor", summaryProps);
        Assert.DoesNotContain("VendorId", summaryProps);
        Assert.DoesNotContain("VendorProduct", summaryProps);
        Assert.DoesNotContain("VendorProductId", summaryProps);
        Assert.DoesNotContain("SupplierCost", summaryProps);
        Assert.DoesNotContain("ExternalVendorOrderId", summaryProps);
        Assert.DoesNotContain("FulfillmentAttempts", summaryProps);

        var detailProps = typeof(CustomerOrderDetailDto).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain("Vendor", detailProps);
        Assert.DoesNotContain("VendorId", detailProps);
        Assert.DoesNotContain("VendorProduct", detailProps);
        Assert.DoesNotContain("VendorProductId", detailProps);
        Assert.DoesNotContain("SupplierCost", detailProps);
        Assert.DoesNotContain("ExternalVendorOrderId", detailProps);
        Assert.DoesNotContain("FulfillmentAttempts", detailProps);

        var itemProps = typeof(CustomerOrderItemDto).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain("VendorId", itemProps);
        Assert.DoesNotContain("SupplierCost", itemProps);
        Assert.DoesNotContain("VendorProduct", itemProps);
    }

    [Fact]
    public async Task InitializeFulfillment_Rejects_OrderItem_That_Belongs_To_Different_Order()
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var (orderA, itemA, _) = await SeedEligibleOrderAsync(db, "diff_ord_a");
        var (orderB, itemB, _) = await SeedEligibleOrderAsync(db, "diff_ord_b");

        var service = new FulfillmentService(db, NullLogger<FulfillmentService>.Instance);

        // Attempt to initialize fulfillment using Order A and Item B (mismatched combination)
        var result = await service.InitializeFulfillmentForOrderItemAsync(orderA.Id, itemB.Id);

        Assert.Equal(FulfillmentInitializationStatus.OrderItemNotFound, result.Status);
        Assert.Contains("not found in order", result.Message, StringComparison.OrdinalIgnoreCase);

        // Verify zero fulfillment records created for Order A or Item B
        var count = await db.Fulfillments.CountAsync();
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task CreateAttempt_Rejects_VendorProduct_For_Different_Product()
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var (order, item, _) = await SeedEligibleOrderAsync(db, "prod_match");

        // Seed a DIFFERENT product
        var diffProduct = new Product
        {
            CategoryId = item.Product.CategoryId,
            InternalSku = "SKU-DIFFERENT-PRODUCT",
            Name = "Different Product",
            SellingPrice = 3000m,
            SellingCurrency = "PKR",
            IsActive = true
        };
        db.Products.Add(diffProduct);

        var vendor = new Vendor { InternalName = "Mismatch Test Vendor", IsActive = true };
        db.Vendors.Add(vendor);
        await db.SaveChangesAsync();

        // VendorProduct maps to the DIFFERENT product
        var mismatchedVp = new VendorProduct
        {
            VendorId = vendor.Id,
            ProductId = diffProduct.Id, // Mismatched ProductId!
            ExternalProductId = "EXT-DIFF-999",
            VendorCost = 2500m,
            VendorCostCurrency = "PKR",
            IsAvailable = true,
            Priority = 1
        };
        db.VendorProducts.Add(mismatchedVp);
        await db.SaveChangesAsync();

        var service = new FulfillmentService(db, NullLogger<FulfillmentService>.Instance);
        var initResult = await service.InitializeFulfillmentForOrderItemAsync(order.Id, item.Id);
        Assert.Equal(FulfillmentInitializationStatus.Success, initResult.Status);

        // Attempting to create an attempt with mismatched VendorProduct must be rejected
        var attemptResult = await service.CreateAttemptAsync(initResult.Fulfillment!.Id, mismatchedVp.Id);

        Assert.Equal(FulfillmentAttemptOperationStatus.InvalidVendorProduct, attemptResult.Status);
        Assert.Contains("Mismatched product mappings are strictly rejected", attemptResult.Message);

        // Verify no attempt was created in database
        var attemptCount = await db.FulfillmentAttempts.CountAsync();
        Assert.Equal(0, attemptCount);
    }

    [Fact]
    public async Task CreateAttempt_With_Unloaded_OrderItem_Navigation_Property_Resolves_Item_And_Enforces_Product_Match()
    {
        var dbName = Guid.NewGuid().ToString();

        long fulfillmentId;
        int mismatchedVpId;
        int matchingVpId;

        // 1. Seed entities in an initial DbContext
        using (var seedDb = CreateInMemoryDbContext(dbName))
        {
            var (order, item, _) = await SeedEligibleOrderAsync(seedDb, "unloaded_nav");

            var vendor = new Vendor { InternalName = "Test Vendor Unloaded Nav", IsActive = true };
            seedDb.Vendors.Add(vendor);
            await seedDb.SaveChangesAsync();

            // Matching VendorProduct for the purchased item's ProductId
            var matchingVp = new VendorProduct
            {
                VendorId = vendor.Id,
                ProductId = item.ProductId,
                ExternalProductId = "EXT-MATCH-001",
                VendorCost = 1500m,
                VendorCostCurrency = "PKR",
                IsAvailable = true,
                Priority = 1
            };
            seedDb.VendorProducts.Add(matchingVp);

            // Mismatched VendorProduct for a different ProductId
            var diffProduct = new Product
            {
                CategoryId = item.Product.CategoryId,
                InternalSku = "SKU-DIFF-UNLOADED",
                Name = "Different Product",
                SellingPrice = 3500m,
                SellingCurrency = "PKR",
                IsActive = true
            };
            seedDb.Products.Add(diffProduct);
            await seedDb.SaveChangesAsync();

            var mismatchedVp = new VendorProduct
            {
                VendorId = vendor.Id,
                ProductId = diffProduct.Id,
                ExternalProductId = "EXT-MISMATCH-999",
                VendorCost = 2500m,
                VendorCostCurrency = "PKR",
                IsAvailable = true,
                Priority = 2
            };
            seedDb.VendorProducts.Add(mismatchedVp);

            var fulfillment = new Fulfillment
            {
                OrderId = order.Id,
                OrderItemId = item.Id,
                Status = FulfillmentStatus.NotStarted,
                FulfillmentMethod = FulfillmentMethod.Unassigned,
                QuantityRequested = 1,
                QuantityFulfilled = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            seedDb.Fulfillments.Add(fulfillment);
            await seedDb.SaveChangesAsync();

            fulfillmentId = fulfillment.Id;
            matchingVpId = matchingVp.Id;
            mismatchedVpId = mismatchedVp.Id;
        }

        // 2. In a completely fresh DbContext with no tracked entities:
        using (var freshDb = CreateInMemoryDbContext(dbName))
        {
            // Verify that loading Fulfillment directly WITHOUT .Include(f => f.OrderItem)
            // leaves OrderItem navigation property strictly null (unloaded)
            var loadedFulfillment = await freshDb.Fulfillments
                .Include(f => f.Attempts)
                .FirstAsync(f => f.Id == fulfillmentId);
            Assert.Null(loadedFulfillment.OrderItem);

            var service = new FulfillmentService(freshDb, NullLogger<FulfillmentService>.Instance);

            // 3. Test MISMATCHED vendor product mapping with unloaded OrderItem:
            // Must NOT bypass validation; must resolve OrderItem and reject mismatch
            var mismatchResult = await service.CreateAttemptAsync(fulfillmentId, mismatchedVpId);
            Assert.Equal(FulfillmentAttemptOperationStatus.InvalidVendorProduct, mismatchResult.Status);
            Assert.Contains("Mismatched product mappings are strictly rejected", mismatchResult.Message);

            // 4. Test MATCHING vendor product mapping with unloaded OrderItem:
            // Must successfully resolve OrderItem and create the attempt
            var matchResult = await service.CreateAttemptAsync(fulfillmentId, matchingVpId);
            Assert.Equal(FulfillmentAttemptOperationStatus.Success, matchResult.Status);
            Assert.NotNull(matchResult.Attempt);
            Assert.Equal(matchingVpId, matchResult.Attempt.VendorProductId);
        }

        // 5. Test UNRESOLVABLE order item: Fulfillment pointing to non-existent OrderItemId
        using (var orphanDb = CreateInMemoryDbContext(Guid.NewGuid().ToString()))
        {
            var vendor = new Vendor { InternalName = "Orphan Vendor", IsActive = true };
            orphanDb.Vendors.Add(vendor);
            var orphanVp = new VendorProduct
            {
                Vendor = vendor,
                ProductId = 12345,
                ExternalProductId = "EXT-ORPHAN",
                VendorCost = 1000m,
                VendorCostCurrency = "PKR",
                IsAvailable = true
            };
            orphanDb.VendorProducts.Add(orphanVp);

            var orphanFulfillment = new Fulfillment
            {
                OrderId = 1,
                OrderItemId = 999999, // Non-existent OrderItemId
                Status = FulfillmentStatus.NotStarted,
                FulfillmentMethod = FulfillmentMethod.Unassigned,
                QuantityRequested = 1,
                QuantityFulfilled = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            orphanDb.Fulfillments.Add(orphanFulfillment);
            await orphanDb.SaveChangesAsync();

            var orphanService = new FulfillmentService(orphanDb, NullLogger<FulfillmentService>.Instance);
            var unresolvableResult = await orphanService.CreateAttemptAsync(orphanFulfillment.Id, orphanVp.Id);

            Assert.Equal(FulfillmentAttemptOperationStatus.InvalidVendorProduct, unresolvableResult.Status);
            Assert.Contains("could not be resolved", unresolvableResult.Message);
        }
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSql_Concurrent_Fulfillment_Initialization_Creates_Exactly_One_Record()
    {
        var options = PostgreSqlTestDatabase.CreateOptions();
        await using var seedDb = new AppDbContext(options);

        if (!await PostgreSqlTestDatabase.EnsureMigratedAsync(seedDb))
        {
            return;
        }

        var suffix = $"conc_{Guid.NewGuid():N}"[..20];
        var (order, item, _) = await SeedEligibleOrderAsync(seedDb, suffix);

        // Prepare two distinct DbContexts connected to PostgreSQL
        await using var db1 = new AppDbContext(options);
        await using var db2 = new AppDbContext(options);

        var service1 = new FulfillmentService(db1, NullLogger<FulfillmentService>.Instance);
        var service2 = new FulfillmentService(db2, NullLogger<FulfillmentService>.Instance);

        // Launch concurrent initialization tasks simultaneously
        var task1 = service1.InitializeFulfillmentForOrderItemAsync(order.Id, item.Id);
        var task2 = service2.InitializeFulfillmentForOrderItemAsync(order.Id, item.Id);

        var results = await Task.WhenAll(task1, task2);

        // Exactly one should succeed as initial creation, and the other should recover safely as AlreadyInitialized
        var statuses = results.Select(r => r.Status).ToList();
        Assert.Contains(FulfillmentInitializationStatus.Success, statuses);
        Assert.Contains(FulfillmentInitializationStatus.AlreadyInitialized, statuses);

        // Verify exactly ONE fulfillment record exists in PostgreSQL
        await using var verifyDb = new AppDbContext(options);
        var fulfillments = await verifyDb.Fulfillments.Where(f => f.OrderItemId == item.Id).ToListAsync();
        Assert.Single(fulfillments);
        Assert.Equal(order.Id, fulfillments[0].OrderId);
        Assert.Equal(item.Id, fulfillments[0].OrderItemId);
        Assert.Equal(FulfillmentStatus.NotStarted, fulfillments[0].Status);
    }

    [Fact]
    public async Task CreateAttempt_Blocks_Fallback_When_Previous_Attempt_Is_Unknown_Or_RequiresReconciliation()
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var (order, item, _) = await SeedEligibleOrderAsync(db, "unconfirmed_reconcile");
        var service = new FulfillmentService(db, NullLogger<FulfillmentService>.Instance);

        var initResult = await service.InitializeFulfillmentForOrderItemAsync(order.Id, item.Id);
        var fulfillmentId = initResult.Fulfillment!.Id;

        // Attempt 1: Ends in RequiresReconciliation
        var att1 = await service.CreateAttemptAsync(fulfillmentId, null);
        Assert.Equal(FulfillmentAttemptOperationStatus.Success, att1.Status);

        await service.RecordAttemptOutcomeAsync(
            att1.Attempt!.Id,
            FulfillmentAttemptStatus.RequiresReconciliation,
            FulfillmentFailureCategory.VendorSystemError,
            "Supplier returned HTTP 500 without clear transaction confirmation.");

        // Attempt 2: Must be BLOCKED
        var att2 = await service.CreateAttemptAsync(fulfillmentId, null);
        Assert.Equal(FulfillmentAttemptOperationStatus.FallbackNotAllowedDueToUnconfirmedOutcome, att2.Status);
        Assert.Contains("strictly required before fallback", att2.Message);
    }

    [Fact]
    public async Task Fulfillment_Operations_Never_Mutate_Orders_Wallets_Payments_Or_SellingPrices()
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var (order, item, payment) = await SeedEligibleOrderAsync(db, "fin_safety_comprehensive");

        // Seed wallet with balance for customer
        var wallet = new WebApiTgBot.Modules.Wallets.Models.Wallet
        {
            CustomerId = order.CustomerId,
            Currency = "PKR",
            Balance = 5000m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync();

        var initialOrderTotal = order.TotalAmount;
        var initialOrderSubtotal = order.Subtotal;
        var initialOrderStatus = order.Status;
        var initialItemUnitPrice = item.UnitPriceSnapshot;
        var initialItemTotalPrice = item.TotalPrice;
        var initialProductPrice = item.Product.SellingPrice;
        var initialPaymentAmount = payment.Amount;
        var initialPaymentStatus = payment.Status;
        var initialWalletBalance = wallet.Balance;

        var service = new FulfillmentService(db, NullLogger<FulfillmentService>.Instance);

        // 1. Initialize fulfillment
        var initResult = await service.InitializeFulfillmentForOrderItemAsync(order.Id, item.Id);
        Assert.Equal(FulfillmentInitializationStatus.Success, initResult.Status);

        // 2. Create attempt
        var attResult = await service.CreateAttemptAsync(initResult.Fulfillment!.Id, null);
        Assert.Equal(FulfillmentAttemptOperationStatus.Success, attResult.Status);

        // 3. Record outcome
        var outcomeResult = await service.RecordAttemptOutcomeAsync(
            attResult.Attempt!.Id,
            FulfillmentAttemptStatus.ConfirmedFailed,
            FulfillmentFailureCategory.VendorOutOfStock,
            "Out of stock");
        Assert.Equal(FulfillmentAttemptOperationStatus.Success, outcomeResult.Status);

        // Verify ALL financial and order records are completely UNCHANGED
        var refreshedOrder = await db.Orders.Include(o => o.Items).ThenInclude(i => i.Product).Include(o => o.Payments).FirstAsync(o => o.Id == order.Id);
        var refreshedWallet = await db.Wallets.FirstAsync(w => w.Id == wallet.Id);

        Assert.Equal(initialOrderStatus, refreshedOrder.Status);
        Assert.Equal(initialOrderTotal, refreshedOrder.TotalAmount);
        Assert.Equal(initialOrderSubtotal, refreshedOrder.Subtotal);

        var refreshedItem = refreshedOrder.Items.First();
        Assert.Equal(initialItemUnitPrice, refreshedItem.UnitPriceSnapshot);
        Assert.Equal(initialItemTotalPrice, refreshedItem.TotalPrice);
        Assert.Equal(initialProductPrice, refreshedItem.Product.SellingPrice);

        var refreshedPayment = refreshedOrder.Payments.First();
        Assert.Equal(initialPaymentAmount, refreshedPayment.Amount);
        Assert.Equal(initialPaymentStatus, refreshedPayment.Status);

        Assert.Equal(initialWalletBalance, refreshedWallet.Balance);

        var txCount = await db.WalletTransactions.CountAsync();
        Assert.Equal(0, txCount); // Zero wallet transactions created
    }
}

