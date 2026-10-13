using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WebApiTgBot.Infrastructure.Data;
using WebApiTgBot.Modules.Catalog.Models;
using WebApiTgBot.Modules.Identity.Models;
using WebApiTgBot.Modules.Orders.Models;
using WebApiTgBot.Modules.Orders.Services;

namespace WebApiTgBot.Tests;

/// <summary>
/// Integration tests verifying the Central Order System Foundation (Milestone 3A):
/// - Order, OrderItem, and OrderStatusHistory entity relationships.
/// - Unique OrderNumber generation and database constraint enforcement.
/// - Product price, SKU, and name snapshots remaining immutable after catalog product updates.
/// - Centralized customer identity relationship (ApplicationUser).
/// - Audit history integrity: tracking previous status, new status, reason, user, and timestamps.
/// - Controlled status transitions via OrderStatusTransitions state machine.
/// - Direct PostgreSQL constraint verification (unique index, foreign keys, cascade deletes).
/// </summary>
public class OrderSystemTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly IOrderNumberGenerator _orderNumberGenerator;

    public OrderSystemTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _orderNumberGenerator = factory.Services.GetRequiredService<IOrderNumberGenerator>();
    }

    private async Task<ApplicationUser> CreateTestCustomerAsync(AppDbContext db, string email)
    {
        var existing = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (existing != null)
        {
            return existing;
        }

        var customer = new ApplicationUser
        {
            UserName = email,
            Email = email,
            FullName = "Test Customer",
            EmailConfirmed = true,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.Users.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    private async Task<Product> CreateTestProductAsync(AppDbContext db, string sku, decimal price, string name = "AI Tool Test")
    {
        var category = await db.Categories.FirstOrDefaultAsync(c => c.Slug == "ai-tools");
        if (category == null)
        {
            category = new Category
            {
                Name = "AI Tools",
                Slug = "ai-tools",
                IsActive = true
            };
            db.Categories.Add(category);
            await db.SaveChangesAsync();
        }

        var product = new Product
        {
            InternalSku = sku,
            Name = name,
            Category = category,
            SellingPrice = price,
            SellingCurrency = "PKR",
            IsActive = true
        };

        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product;
    }

    [Fact]
    public void OrderNumberGenerator_GeneratesStandardizedUniqueNumbers()
    {
        // Act
        var orderNumber1 = _orderNumberGenerator.GenerateOrderNumber();
        var orderNumber2 = _orderNumberGenerator.GenerateOrderNumber();

        // Assert
        Assert.StartsWith("ORD-", orderNumber1);
        Assert.StartsWith("ORD-", orderNumber2);
        Assert.NotEqual(orderNumber1, orderNumber2);
        Assert.Matches(@"^ORD-\d{8}-[2-9A-HJ-NP-Z]{6}$", orderNumber1);
    }

    [Fact]
    public async Task Order_CreatesWithItemsAndStatusHistory_PersistsRelationships()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var customer = await CreateTestCustomerAsync(db, $"cust_{Guid.NewGuid():N}@test.local");
        var product = await CreateTestProductAsync(db, $"SKU_{Guid.NewGuid():N}", 5000.00m, "ChatGPT Plus 1 Month");

        var orderNumber = _orderNumberGenerator.GenerateOrderNumber();

        // Act — Create Order with OrderItem and initial OrderStatusHistory
        var order = new Order
        {
            OrderNumber = orderNumber,
            CustomerId = customer.Id,
            Customer = customer,
            Status = OrderStatus.PendingPayment,
            Currency = "PKR",
            Subtotal = 5000.00m,
            TotalAmount = 5000.00m,
            SalesChannel = SalesChannel.Website,
            CustomerNotes = "Please deliver promptly"
        };

        var orderItem = new OrderItem
        {
            Order = order,
            Product = product,
            ProductId = product.Id,
            ProductSkuSnapshot = product.InternalSku,
            ProductNameSnapshot = product.Name,
            Quantity = 1,
            UnitPriceSnapshot = product.SellingPrice,
            Currency = "PKR",
            TotalPrice = 5000.00m
        };
        order.Items.Add(orderItem);

        var history = new OrderStatusHistory
        {
            Order = order,
            PreviousStatus = null,
            NewStatus = OrderStatus.PendingPayment,
            Reason = "Order created via website checkout",
            ChangedByUserId = customer.Id
        };
        order.StatusHistory.Add(history);

        db.Orders.Add(order);
        await db.SaveChangesAsync();

        // Assert — Query back with full graph
        var loadedOrder = await db.Orders
            .Include(o => o.Customer)
            .Include(o => o.Items)
                .ThenInclude(i => i.Product)
            .Include(o => o.StatusHistory)
            .FirstOrDefaultAsync(o => o.Id == order.Id);

        Assert.NotNull(loadedOrder);
        Assert.Equal(orderNumber, loadedOrder.OrderNumber);
        Assert.Equal(customer.Id, loadedOrder.Customer.Id);
        Assert.Equal(OrderStatus.PendingPayment, loadedOrder.Status);
        Assert.Equal(SalesChannel.Website, loadedOrder.SalesChannel);
        Assert.Single(loadedOrder.Items);
        Assert.Equal(5000.00m, loadedOrder.Items.First().TotalPrice);
        Assert.Single(loadedOrder.StatusHistory);
        Assert.Null(loadedOrder.StatusHistory.First().PreviousStatus);
        Assert.Equal(OrderStatus.PendingPayment, loadedOrder.StatusHistory.First().NewStatus);
    }

    [Fact]
    public async Task PriceSnapshot_RemainsUnchanged_WhenCatalogProductIsUpdatedLater()
    {
        // Arrange — Place order capturing product price snapshot of 6,000 PKR
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var customer = await CreateTestCustomerAsync(db, $"cust_price_{Guid.NewGuid():N}@test.local");
        var product = await CreateTestProductAsync(db, $"SKU_SNAP_{Guid.NewGuid():N}", 6000.00m, "Original Product Name");

        var order = new Order
        {
            OrderNumber = _orderNumberGenerator.GenerateOrderNumber(),
            CustomerId = customer.Id,
            Status = OrderStatus.PendingPayment,
            Currency = "PKR",
            Subtotal = 12000.00m,
            TotalAmount = 12000.00m,
            SalesChannel = SalesChannel.Website
        };

        var orderItem = new OrderItem
        {
            Order = order,
            ProductId = product.Id,
            ProductSkuSnapshot = product.InternalSku,
            ProductNameSnapshot = product.Name,
            Quantity = 2,
            UnitPriceSnapshot = product.SellingPrice, // 6,000 PKR
            Currency = "PKR",
            TotalPrice = 12000.00m
        };
        order.Items.Add(orderItem);
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        // Act — Admin updates product in catalog to 8,500 PKR and renames product
        product.SellingPrice = 8500.00m;
        product.Name = "Renamed Product With Higher Price";
        await db.SaveChangesAsync();

        // Assert — Historical order item snapshots remain 100% unaltered
        var historicalItem = await db.OrderItems
            .AsNoTracking()
            .FirstOrDefaultAsync(oi => oi.Id == orderItem.Id);

        Assert.NotNull(historicalItem);
        Assert.Equal(6000.00m, historicalItem.UnitPriceSnapshot); // Retains 6,000 PKR snapshot
        Assert.Equal(12000.00m, historicalItem.TotalPrice);       // Retains 12,000 PKR total
        Assert.Equal("Original Product Name", historicalItem.ProductNameSnapshot); // Retains original name
        Assert.Equal(product.InternalSku, historicalItem.ProductSkuSnapshot);
    }

    [Fact]
    public async Task StatusHistory_RecordsSequentialTransitions()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var customer = await CreateTestCustomerAsync(db, $"cust_trans_{Guid.NewGuid():N}@test.local");
        var product = await CreateTestProductAsync(db, $"SKU_TRANS_{Guid.NewGuid():N}", 3000.00m);

        var order = new Order
        {
            OrderNumber = _orderNumberGenerator.GenerateOrderNumber(),
            CustomerId = customer.Id,
            Status = OrderStatus.PendingPayment,
            Currency = "PKR",
            Subtotal = 3000.00m,
            TotalAmount = 3000.00m,
            SalesChannel = SalesChannel.Telegram
        };
        order.StatusHistory.Add(new OrderStatusHistory
        {
            PreviousStatus = null,
            NewStatus = OrderStatus.PendingPayment,
            Reason = "Initial creation via Telegram bot",
            ChangedByUserId = customer.Id
        });
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        // Act 1 — Transition PendingPayment -> Paid
        Assert.True(OrderStatusTransitions.IsValidTransition(order.Status, OrderStatus.Paid));
        var prev1 = order.Status;
        order.Status = OrderStatus.Paid;
        order.StatusHistory.Add(new OrderStatusHistory
        {
            OrderId = order.Id,
            PreviousStatus = prev1,
            NewStatus = OrderStatus.Paid,
            Reason = "JazzCash payment verified",
            ChangedByUserId = "system"
        });
        await db.SaveChangesAsync();

        // Act 2 — Transition Paid -> Processing
        Assert.True(OrderStatusTransitions.IsValidTransition(order.Status, OrderStatus.Processing));
        var prev2 = order.Status;
        order.Status = OrderStatus.Processing;
        order.StatusHistory.Add(new OrderStatusHistory
        {
            OrderId = order.Id,
            PreviousStatus = prev2,
            NewStatus = OrderStatus.Processing,
            Reason = "Admin queued fulfillment",
            ChangedByUserId = "admin"
        });
        await db.SaveChangesAsync();

        // Act 3 — Transition Processing -> Completed
        Assert.True(OrderStatusTransitions.IsValidTransition(order.Status, OrderStatus.Completed));
        var prev3 = order.Status;
        order.Status = OrderStatus.Completed;
        order.StatusHistory.Add(new OrderStatusHistory
        {
            OrderId = order.Id,
            PreviousStatus = prev3,
            NewStatus = OrderStatus.Completed,
            Reason = "Voucher code delivered to customer",
            ChangedByUserId = "admin"
        });
        await db.SaveChangesAsync();

        // Assert — Exactly 4 historical audit records exist in chronological order
        var historyRecords = await db.OrderStatusHistories
            .Where(h => h.OrderId == order.Id)
            .OrderBy(h => h.Id)
            .ToListAsync();

        Assert.Equal(4, historyRecords.Count);
        Assert.Null(historyRecords[0].PreviousStatus);
        Assert.Equal(OrderStatus.PendingPayment, historyRecords[0].NewStatus);

        Assert.Equal(OrderStatus.PendingPayment, historyRecords[1].PreviousStatus);
        Assert.Equal(OrderStatus.Paid, historyRecords[1].NewStatus);

        Assert.Equal(OrderStatus.Paid, historyRecords[2].PreviousStatus);
        Assert.Equal(OrderStatus.Processing, historyRecords[2].NewStatus);

        Assert.Equal(OrderStatus.Processing, historyRecords[3].PreviousStatus);
        Assert.Equal(OrderStatus.Completed, historyRecords[3].NewStatus);
    }

    [Theory]
    [InlineData(OrderStatus.PendingPayment, OrderStatus.Paid, true)]
    [InlineData(OrderStatus.PendingPayment, OrderStatus.Cancelled, true)]
    [InlineData(OrderStatus.PendingPayment, OrderStatus.Failed, true)]
    [InlineData(OrderStatus.PendingPayment, OrderStatus.Completed, false)] // Cannot skip directly to Completed
    [InlineData(OrderStatus.Paid, OrderStatus.Processing, true)]
    [InlineData(OrderStatus.Paid, OrderStatus.Cancelled, true)]
    [InlineData(OrderStatus.Processing, OrderStatus.SentToVendor, true)]
    [InlineData(OrderStatus.Processing, OrderStatus.Completed, true)]
    [InlineData(OrderStatus.Cancelled, OrderStatus.Paid, false)]          // Terminal state cannot reopen
    [InlineData(OrderStatus.Refunded, OrderStatus.Processing, false)]     // Terminal state cannot reopen
    [InlineData(OrderStatus.Completed, OrderStatus.RefundPending, true)]   // Dispute handling allowed
    public void OrderStatusTransitions_EnforcesControlledLifecycle(
        OrderStatus current,
        OrderStatus target,
        bool expectedValid)
    {
        var isValid = OrderStatusTransitions.IsValidTransition(current, target);
        Assert.Equal(expectedValid, isValid);
    }

    [Fact]
    public async Task PostgreSql_EnforcesUniqueOrderNumber_AndForeignKeys()
    {
        // Connect directly to the PostgreSQL database to verify schema constraints
        var options = PostgreSqlTestDatabase.CreateOptions();
        await using var db = new AppDbContext(options);

        if (!await PostgreSqlTestDatabase.EnsureMigratedAsync(db))
        {
            return;
        }

        var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        var customerEmail = $"pg_cust_{uniqueSuffix}@test.local";

        // 1. Setup Customer and Category/Product in PostgreSQL
        var customer = new ApplicationUser
        {
            UserName = customerEmail,
            Email = customerEmail,
            FullName = "PG Customer",
            EmailConfirmed = true,
            IsActive = true
        };
        db.Users.Add(customer);

        var category = new Category
        {
            Name = "PG Cat " + uniqueSuffix,
            Slug = "pg-cat-" + uniqueSuffix,
            IsActive = true
        };
        db.Categories.Add(category);

        var product = new Product
        {
            InternalSku = "PG_ORDER_SKU_" + uniqueSuffix.ToUpperInvariant(),
            Name = "PG Order Product",
            Category = category,
            SellingPrice = 4500m,
            SellingCurrency = "PKR",
            IsActive = true
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();

        var duplicateOrderNumber = $"ORD-PGTEST-{uniqueSuffix.ToUpperInvariant()}";

        try
        {
            // 2. Insert valid order in PostgreSQL
            var order1 = new Order
            {
                OrderNumber = duplicateOrderNumber,
                CustomerId = customer.Id,
                Status = OrderStatus.PendingPayment,
                Currency = "PKR",
                Subtotal = 4500m,
                TotalAmount = 4500m,
                SalesChannel = SalesChannel.Website
            };

            var item1 = new OrderItem
            {
                Order = order1,
                ProductId = product.Id,
                ProductSkuSnapshot = product.InternalSku,
                ProductNameSnapshot = product.Name,
                Quantity = 1,
                UnitPriceSnapshot = 4500m,
                Currency = "PKR",
                TotalPrice = 4500m
            };
            order1.Items.Add(item1);

            var history1 = new OrderStatusHistory
            {
                Order = order1,
                PreviousStatus = null,
                NewStatus = OrderStatus.PendingPayment,
                Reason = "PG test order",
                ChangedByUserId = customer.Id
            };
            order1.StatusHistory.Add(history1);

            db.Orders.Add(order1);
            await db.SaveChangesAsync();

            // 3. Attempt to insert duplicate OrderNumber into PostgreSQL
            var duplicateOrder = new Order
            {
                OrderNumber = duplicateOrderNumber, // Same OrderNumber!
                CustomerId = customer.Id,
                Status = OrderStatus.PendingPayment,
                Currency = "PKR",
                Subtotal = 4500m,
                TotalAmount = 4500m,
                SalesChannel = SalesChannel.Telegram
            };
            db.Orders.Add(duplicateOrder);

            // Assert: PostgreSQL must reject with DbUpdateException wrapping PostgresException 23505 (unique_violation)
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.NotNull(ex.InnerException);
            var pgEx = Assert.IsType<PostgresException>(ex.InnerException);
            Assert.Equal("23505", pgEx.SqlState);
            Assert.Contains("IX_Orders_OrderNumber", pgEx.ConstraintName ?? pgEx.Message);
        }
        finally
        {
            // Clean up test records
            await using var cleanupDb = new AppDbContext(options);

            // Because of DeleteBehavior.Restrict, child records must be explicitly deleted before parent order
            var orders = await cleanupDb.Orders
                .Include(o => o.Items)
                .Include(o => o.StatusHistory)
                .Where(o => o.OrderNumber == duplicateOrderNumber)
                .ToListAsync();

            foreach (var o in orders)
            {
                cleanupDb.OrderItems.RemoveRange(o.Items);
                cleanupDb.OrderStatusHistories.RemoveRange(o.StatusHistory);
            }
            cleanupDb.Orders.RemoveRange(orders);

            var prod = await cleanupDb.Products.FindAsync(product.Id);
            if (prod != null) cleanupDb.Products.Remove(prod);

            var cat = await cleanupDb.Categories.FindAsync(category.Id);
            if (cat != null) cleanupDb.Categories.Remove(cat);

            var cust = await cleanupDb.Users.FindAsync(customer.Id);
            if (cust != null) cleanupDb.Users.Remove(cust);

            await cleanupDb.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Order_CannotBeDeleted_WhenOrderItemsExist_EnforcesRestrictDelete()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var customer = await CreateTestCustomerAsync(db, $"cust_del_{Guid.NewGuid():N}@test.local");
        var product = await CreateTestProductAsync(db, $"SKU_DEL_{Guid.NewGuid():N}", 1500.00m);

        var order = new Order
        {
            OrderNumber = _orderNumberGenerator.GenerateOrderNumber(),
            CustomerId = customer.Id,
            Status = OrderStatus.PendingPayment,
            Currency = "PKR",
            Subtotal = 1500.00m,
            TotalAmount = 1500.00m,
            SalesChannel = SalesChannel.Website
        };

        var orderItem = new OrderItem
        {
            Order = order,
            ProductId = product.Id,
            ProductSkuSnapshot = product.InternalSku,
            ProductNameSnapshot = product.Name,
            Quantity = 1,
            UnitPriceSnapshot = product.SellingPrice,
            Currency = "PKR",
            TotalPrice = 1500.00m
        };
        order.Items.Add(orderItem);
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        // Act & Assert — Attempting to remove the parent Order while dependent items exist
        // must throw InvalidOperationException due to DeleteBehavior.Restrict
        Assert.Throws<InvalidOperationException>(() => db.Orders.Remove(order));

        // Verify neither order nor item was removed
        var orderExists = await db.Orders.AnyAsync(o => o.Id == order.Id);
        var itemExists = await db.OrderItems.AnyAsync(oi => oi.Id == orderItem.Id);
        Assert.True(orderExists);
        Assert.True(itemExists);
    }

    [Fact]
    public async Task Order_CannotBeDeleted_WhenStatusHistoryExists_EnforcesRestrictDelete()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var customer = await CreateTestCustomerAsync(db, $"cust_histdel_{Guid.NewGuid():N}@test.local");

        var order = new Order
        {
            OrderNumber = _orderNumberGenerator.GenerateOrderNumber(),
            CustomerId = customer.Id,
            Status = OrderStatus.PendingPayment,
            Currency = "PKR",
            Subtotal = 2000.00m,
            TotalAmount = 2000.00m,
            SalesChannel = SalesChannel.Telegram
        };

        var history = new OrderStatusHistory
        {
            Order = order,
            PreviousStatus = null,
            NewStatus = OrderStatus.PendingPayment,
            Reason = "Initial order placement",
            ChangedByUserId = customer.Id
        };
        order.StatusHistory.Add(history);
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        // Act & Assert — Attempting to remove the parent Order while audit history exists
        // must throw InvalidOperationException due to DeleteBehavior.Restrict
        Assert.Throws<InvalidOperationException>(() => db.Orders.Remove(order));

        // Verify audit history is permanently preserved
        var historyExists = await db.OrderStatusHistories.AnyAsync(h => h.Id == history.Id);
        Assert.True(historyExists);
    }

    [Fact]
    public async Task PostgreSql_EnforcesRestrictDelete_OnOrdersWithItemsAndHistory()
    {
        var options = PostgreSqlTestDatabase.CreateOptions();
        await using var db = new AppDbContext(options);

        if (!await PostgreSqlTestDatabase.EnsureMigratedAsync(db))
        {
            return;
        }

        var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        var customerEmail = $"pg_restrict_{uniqueSuffix}@test.local";

        var customer = new ApplicationUser
        {
            UserName = customerEmail,
            Email = customerEmail,
            FullName = "PG Restrict Customer",
            EmailConfirmed = true,
            IsActive = true
        };
        db.Users.Add(customer);

        var category = new Category
        {
            Name = "PG Restrict Cat " + uniqueSuffix,
            Slug = "pg-restrict-cat-" + uniqueSuffix,
            IsActive = true
        };
        db.Categories.Add(category);

        var product = new Product
        {
            InternalSku = "PG_RESTRICT_SKU_" + uniqueSuffix.ToUpperInvariant(),
            Name = "PG Restrict Product",
            Category = category,
            SellingPrice = 3000m,
            SellingCurrency = "PKR",
            IsActive = true
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();

        var orderNumber = $"ORD-RESTRICT-{uniqueSuffix.ToUpperInvariant()}";
        var order = new Order
        {
            OrderNumber = orderNumber,
            CustomerId = customer.Id,
            Status = OrderStatus.PendingPayment,
            Currency = "PKR",
            Subtotal = 3000m,
            TotalAmount = 3000m,
            SalesChannel = SalesChannel.Website
        };

        var orderItem = new OrderItem
        {
            Order = order,
            ProductId = product.Id,
            ProductSkuSnapshot = product.InternalSku,
            ProductNameSnapshot = product.Name,
            Quantity = 1,
            UnitPriceSnapshot = 3000m,
            Currency = "PKR",
            TotalPrice = 3000m
        };
        order.Items.Add(orderItem);

        var history = new OrderStatusHistory
        {
            Order = order,
            PreviousStatus = null,
            NewStatus = OrderStatus.PendingPayment,
            Reason = "Postgres restrict delete test",
            ChangedByUserId = customer.Id
        };
        order.StatusHistory.Add(history);

        db.Orders.Add(order);
        await db.SaveChangesAsync();

        try
        {
            // Act: In a separate DbContext instance without tracking child entities,
            // attempt to delete the order directly from PostgreSQL
            await using var deleteAttemptDb = new AppDbContext(options);
            var orderStub = new Order { Id = order.Id };
            deleteAttemptDb.Orders.Attach(orderStub);
            deleteAttemptDb.Orders.Remove(orderStub);

            // Assert: PostgreSQL must reject the deletion with foreign key violation (SqlState 23503)
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => deleteAttemptDb.SaveChangesAsync());
            Assert.NotNull(ex.InnerException);
            var pgEx = Assert.IsType<PostgresException>(ex.InnerException);
            Assert.Equal("23503", pgEx.SqlState); // foreign_key_violation

            // Verify order, line items, and audit history remain untouched in PostgreSQL
            await using var verifyDb = new AppDbContext(options);
            var preservedOrder = await verifyDb.Orders
                .Include(o => o.Items)
                .Include(o => o.StatusHistory)
                .FirstOrDefaultAsync(o => o.Id == order.Id);

            Assert.NotNull(preservedOrder);
            Assert.Single(preservedOrder.Items);
            Assert.Single(preservedOrder.StatusHistory);
        }
        finally
        {
            // Clean up: explicitly remove children first, then parent order
            await using var cleanupDb = new AppDbContext(options);
            var orderWithChildren = await cleanupDb.Orders
                .Include(o => o.Items)
                .Include(o => o.StatusHistory)
                .FirstOrDefaultAsync(o => o.Id == order.Id);

            if (orderWithChildren != null)
            {
                cleanupDb.OrderItems.RemoveRange(orderWithChildren.Items);
                cleanupDb.OrderStatusHistories.RemoveRange(orderWithChildren.StatusHistory);
                cleanupDb.Orders.Remove(orderWithChildren);
            }

            var prod = await cleanupDb.Products.FindAsync(product.Id);
            if (prod != null) cleanupDb.Products.Remove(prod);

            var cat = await cleanupDb.Categories.FindAsync(category.Id);
            if (cat != null) cleanupDb.Categories.Remove(cat);

            var cust = await cleanupDb.Users.FindAsync(customer.Id);
            if (cust != null) cleanupDb.Users.Remove(cust);

            await cleanupDb.SaveChangesAsync();
        }
    }
}
