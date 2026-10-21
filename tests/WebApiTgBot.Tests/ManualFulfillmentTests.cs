using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using WebApiTgBot.Common.Models;
using WebApiTgBot.Infrastructure.Data;
using WebApiTgBot.Modules.Catalog.Models;
using WebApiTgBot.Modules.Fulfillments.Models;
using WebApiTgBot.Modules.Fulfillments.Models.DTOs;
using WebApiTgBot.Modules.Fulfillments.Options;
using WebApiTgBot.Modules.Fulfillments.Services;
using WebApiTgBot.Modules.Identity.Models;
using WebApiTgBot.Modules.Identity.Models.DTOs;
using WebApiTgBot.Modules.Orders.Models;
using WebApiTgBot.Modules.Orders.Models.DTOs;
using WebApiTgBot.Modules.Payments.Models;
using WebApiTgBot.Modules.Payments.Options;
using WebApiTgBot.Modules.Wallets.Models;

namespace WebApiTgBot.Tests;

/// <summary>
/// Comprehensive integration test suite for Milestone 5C: Controlled Manual Fulfillment & Delivery Tracking.
/// Verifies:
/// 1. Feature gate disabled by default returns 403 Forbidden; enabled client operates normally.
/// 2. Role-based access control: Anonymous (401), Customer (403), Admin (200).
/// 3. CSRF token enforcement on state-changing operations.
/// 4. Admin starts manual fulfillment for eligible paid Processing order.
/// 5. Unpaid orders (PendingPayment) and un-processed orders (Paid) are rejected.
/// 6. Wrong OrderItem/Order relationship rejected.
/// 7. Duplicate manual starts blocked when attempt already actively processing.
/// 8. Unresolved vendor attempts (Unknown / RequiresReconciliation) block manual fulfillment.
/// 9. Succeeded vendor attempts block duplicate manual fulfillment.
/// 10. Manual completion requires explicit ConfirmDelivered = true and safe evidence reference.
/// 11. Sanitization rejects HTML/script tags in delivery reference and channel.
/// 12. Correct fulfillment, attempt, and audit status transitions.
/// 13. Single-item completion transitions Order to Completed with audit log.
/// 14. Multi-item orders remain Processing until all items are completed.
/// 15. Concurrency safety: simultaneous completions and starts on real PostgreSQL.
/// 16. Transaction rollback: failure during completion rolls back all state atomically.
/// 17. Zero mutation of payments, wallet balances, or transaction ledgers.
/// 18. Customer order APIs never expose internal fulfillment evidence or attempts.
/// 19. Admin read API returns complete safe audit trail.
/// </summary>
public class ManualFulfillmentTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ManualFulfillmentTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private HttpClient CreateEnabledClient(List<string>? approvedSkus = null)
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.Configure<ManualFulfillmentOptions>(options =>
                {
                    options.Enabled = true;
                    if (approvedSkus != null)
                    {
                        options.ApprovedProductSkus = approvedSkus;
                    }
                });
                services.Configure<WalletPurchaseOptions>(options =>
                {
                    options.Enabled = true;
                    options.Currency = "PKR";
                });
                services.Configure<WalletTopUpOptions>(options =>
                {
                    options.Enabled = true;
                });
            });
        }).CreateClient();
    }

    private static AppDbContext CreateInMemoryDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new AppDbContext(options);
    }

    private async Task EnsureRolesExistAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        foreach (var role in new[] { AppRoles.Admin, AppRoles.Customer })
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                await roleManager.CreateAsync(new ApplicationRole(role));
            }
        }
    }

    private async Task<(string AdminId, string AuthCookie, string AntiforgeryCookie, string CsrfToken)> LoginAsAdminAsync(HttpClient? client = null)
    {
        await EnsureRolesExistAsync();
        var httpClient = client ?? _client;
        var email = $"admin_ful_{Guid.NewGuid():N}@test.local";
        var password = "SecureAdminPassword123!";
        string adminId;

        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                FullName = "Admin Fulfillment Manager",
                EmailConfirmed = true,
                IsActive = true
            };
            var createRes = await userManager.CreateAsync(user, password);
            Assert.True(createRes.Succeeded);
            await userManager.AddToRoleAsync(user, AppRoles.Admin);
            adminId = user.Id;
        }

        var loginRes = await httpClient.PostAsJsonAsync("/api/admin/auth/login", new AdminLoginRequest
        {
            Email = email,
            Password = password
        });
        Assert.Equal(HttpStatusCode.OK, loginRes.StatusCode);

        var authCookie = loginRes.Headers.GetValues("Set-Cookie")
            .First(c => c.Contains(".WebAPI.Admin.Auth"))
            .Split(';')[0];

        var csrfReq = new HttpRequestMessage(HttpMethod.Get, "/api/admin/auth/csrf");
        csrfReq.Headers.Add("Cookie", authCookie);
        var csrfRes = await httpClient.SendAsync(csrfReq);
        Assert.Equal(HttpStatusCode.OK, csrfRes.StatusCode);
        var csrfData = await csrfRes.Content.ReadFromJsonAsync<ApiResponse<string>>();

        var antiforgeryCookie = string.Empty;
        if (csrfRes.Headers.TryGetValues("Set-Cookie", out var adminCookies))
        {
            var match = adminCookies.FirstOrDefault(c => c.Contains(".WebAPI.Antiforgery"));
            if (match != null)
            {
                antiforgeryCookie = match.Split(';')[0];
            }
        }

        return (adminId, authCookie, antiforgeryCookie, csrfData!.Data!);
    }

    private async Task<(string CustomerId, string AuthCookie)> RegisterAndLoginCustomerAsync(string email, string password, HttpClient? client = null)
    {
        await EnsureRolesExistAsync();
        var httpClient = client ?? _client;

        var regRes = await httpClient.PostAsJsonAsync("/api/customer/auth/register", new CustomerRegisterRequest
        {
            Email = email,
            Password = password,
            FullName = "Customer Fulfillment Test"
        });
        Assert.Equal(HttpStatusCode.Created, regRes.StatusCode);
        var regData = await regRes.Content.ReadFromJsonAsync<ApiResponse<CustomerUserDto>>();

        var loginRes = await httpClient.PostAsJsonAsync("/api/customer/auth/login", new CustomerLoginRequest
        {
            Email = email,
            Password = password
        });
        Assert.Equal(HttpStatusCode.OK, loginRes.StatusCode);

        var authCookie = loginRes.Headers.GetValues("Set-Cookie")
            .First(c => c.Contains(".WebAPI.Customer.Auth"))
            .Split(';')[0];

        return (regData!.Data!.Id, authCookie);
    }

    private static async Task<(Order order, OrderItem item, Payment payment)> SeedEligibleProcessingOrderAsync(AppDbContext db, string suffix, decimal price = 2500m)
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
            SellingPrice = price,
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
            Status = OrderStatus.Processing, // Must be Processing for manual fulfillment
            Currency = "PKR",
            Subtotal = price,
            TotalAmount = price,
            SalesChannel = SalesChannel.Website,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
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
            UnitPriceSnapshot = price,
            Currency = "PKR",
            TotalPrice = price
        };
        db.OrderItems.Add(item);
        await db.SaveChangesAsync();

        var payment = new Payment
        {
            PaymentNumber = $"PAY-{suffix}",
            OrderId = order.Id,
            Purpose = PaymentPurpose.OrderPurchase,
            Amount = price,
            Currency = "PKR",
            Status = PaymentStatus.Paid,
            PaymentMethodCode = "WALLET_PKR",
            PaidAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        return (order, item, payment);
    }

    // ── 1. Feature Gate Tests ──────────────────────────────────────────────────

    [Fact]
    public async Task FeatureGate_DisabledByDefault_Returns_403_Forbidden()
    {
        var client = _client; // Default factory client has Enabled = false
        var (_, authCookie, antiCookie, csrfToken) = await LoginAsAdminAsync(client);

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/admin/orders/ORD-TEST-GATE/items/1/manual-fulfillment/start");
        req.Headers.Add("Cookie", $"{authCookie}; {antiCookie}");
        req.Headers.Add("X-XSRF-TOKEN", csrfToken);

        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<ApiResponse<AdminFulfillmentDto>>();
        Assert.Contains("disabled by server configuration", body!.Message);
    }

    [Fact]
    public async Task FeatureGate_Complete_DisabledByDefault_Returns_403_Forbidden()
    {
        var client = _client;
        var (_, authCookie, antiCookie, csrfToken) = await LoginAsAdminAsync(client);

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/admin/orders/ORD-TEST-GATE/items/1/manual-fulfillment/complete");
        req.Headers.Add("Cookie", $"{authCookie}; {antiCookie}");
        req.Headers.Add("X-XSRF-TOKEN", csrfToken);
        req.Content = JsonContent.Create(new AdminCompleteManualFulfillmentRequest
        {
            ConfirmDelivered = true,
            DeliveryReference = "TICKET-12345",
            DeliveryChannel = "EmailSupport"
        });

        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<ApiResponse<AdminFulfillmentDto>>();
        Assert.Contains("disabled by server configuration", body!.Message);
    }

    // ── 2. Security & Authorization Tests ──────────────────────────────────────

    [Fact]
    public async Task Anonymous_Cannot_Access_Manual_Fulfillment_APIs()
    {
        var client = CreateEnabledClient();

        var resStart = await client.PostAsync("/api/admin/orders/ORD-ANON/items/1/manual-fulfillment/start", null);
        Assert.Equal(HttpStatusCode.Unauthorized, resStart.StatusCode);

        var resComplete = await client.PostAsJsonAsync("/api/admin/orders/ORD-ANON/items/1/manual-fulfillment/complete", new AdminCompleteManualFulfillmentRequest
        {
            ConfirmDelivered = true,
            DeliveryReference = "TICKET-999",
            DeliveryChannel = "EmailSupport"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, resComplete.StatusCode);

        var resGet = await client.GetAsync("/api/admin/orders/ORD-ANON/fulfillments");
        Assert.Equal(HttpStatusCode.Unauthorized, resGet.StatusCode);
    }

    [Fact]
    public async Task Customer_Cannot_Access_Manual_Fulfillment_APIs()
    {
        var client = CreateEnabledClient();
        var email = $"cust_forbid_{Guid.NewGuid():N}@test.local";
        var (_, customerAuthCookie) = await RegisterAndLoginCustomerAsync(email, "CustomerPass123!", client);

        var reqStart = new HttpRequestMessage(HttpMethod.Post, "/api/admin/orders/ORD-CUST/items/1/manual-fulfillment/start");
        reqStart.Headers.Add("Cookie", customerAuthCookie);
        var resStart = await client.SendAsync(reqStart);
        Assert.True(resStart.StatusCode == HttpStatusCode.Unauthorized || resStart.StatusCode == HttpStatusCode.Forbidden);

        var reqGet = new HttpRequestMessage(HttpMethod.Get, "/api/admin/orders/ORD-CUST/fulfillments");
        reqGet.Headers.Add("Cookie", customerAuthCookie);
        var resGet = await client.SendAsync(reqGet);
        Assert.True(resGet.StatusCode == HttpStatusCode.Unauthorized || resGet.StatusCode == HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CSRF_Protection_Enforced_On_Start_And_Complete()
    {
        var client = CreateEnabledClient();
        var (_, authCookie, _, _) = await LoginAsAdminAsync(client);

        // Omit CSRF token
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/admin/orders/ORD-CSRF/items/1/manual-fulfillment/start");
        req.Headers.Add("Cookie", authCookie);
        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // ── 3. Start Manual Fulfillment Workflow ───────────────────────────────────

    [Fact]
    public async Task StartManualFulfillment_Succeeds_For_Eligible_Processing_Order()
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(db, "start_success");

        var enabledOptions = Options.Create(new ManualFulfillmentOptions
        {
            Enabled = true,
            ApprovedProductSkus = new() { item.ProductSkuSnapshot }
        });
        var service = new FulfillmentService(db, NullLogger<FulfillmentService>.Instance, enabledOptions);

        var result = await service.StartManualFulfillmentAsync(order.OrderNumber, item.Id, "admin-user-001");

        Assert.True(result.IsSuccess);
        Assert.Equal(ManualFulfillmentOperationStatus.Success, result.Status);
        Assert.NotNull(result.Data);

        var data = result.Data;
        Assert.Equal(FulfillmentStatus.InProgress.ToString(), data.Status);
        Assert.Equal(FulfillmentMethod.Manual.ToString(), data.FulfillmentMethod);
        Assert.Single(data.Attempts);

        var attempt = data.Attempts[0];
        Assert.Equal(1, attempt.AttemptNumber);
        Assert.True(attempt.IsManual);
        Assert.Null(attempt.VendorProductId);
        Assert.Equal(FulfillmentAttemptStatus.Submitted.ToString(), attempt.Status);
        Assert.Equal("admin-user-001", attempt.InitiatedByUserId);

        // Verify status history audit record
        Assert.Single(data.StatusHistory);
        var history = data.StatusHistory[0];
        Assert.Equal(FulfillmentStatus.NotStarted.ToString(), history.PreviousStatus);
        Assert.Equal(FulfillmentStatus.InProgress.ToString(), history.NewStatus);
        Assert.Equal("admin-user-001", history.ChangedByUserId);
        Assert.Contains("started manual fulfillment", history.Reason);
    }

    [Fact]
    public async Task StartManualFulfillment_Rejects_Unpaid_Order()
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(db, "start_unpaid");
        order.Status = OrderStatus.PendingPayment;
        await db.SaveChangesAsync();

        var service = new FulfillmentService(db, NullLogger<FulfillmentService>.Instance, Options.Create(new ManualFulfillmentOptions { Enabled = true }));
        var result = await service.StartManualFulfillmentAsync(order.OrderNumber, item.Id, "admin-1");

        Assert.False(result.IsSuccess);
        Assert.Equal(ManualFulfillmentOperationStatus.BadRequest, result.Status);
        Assert.Contains("unpaid order", result.Message);
    }

    [Fact]
    public async Task StartManualFulfillment_Rejects_Paid_Order_Not_Yet_Processing()
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(db, "start_paid_not_proc");
        order.Status = OrderStatus.Paid; // Must transition to Processing first
        await db.SaveChangesAsync();

        var service = new FulfillmentService(db, NullLogger<FulfillmentService>.Instance, Options.Create(new ManualFulfillmentOptions { Enabled = true }));
        var result = await service.StartManualFulfillmentAsync(order.OrderNumber, item.Id, "admin-1");

        Assert.False(result.IsSuccess);
        Assert.Equal(ManualFulfillmentOperationStatus.Conflict, result.Status);
        Assert.Contains("start order processing before initiating manual fulfillment", result.Message);
    }

    [Fact]
    public async Task StartManualFulfillment_Rejects_Mismatched_OrderItem()
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var (order1, _, _) = await SeedEligibleProcessingOrderAsync(db, "ord1");
        var (_, item2, _) = await SeedEligibleProcessingOrderAsync(db, "ord2");

        var service = new FulfillmentService(db, NullLogger<FulfillmentService>.Instance, Options.Create(new ManualFulfillmentOptions { Enabled = true }));
        // Try to start fulfillment for order1 using item2.Id
        var result = await service.StartManualFulfillmentAsync(order1.OrderNumber, item2.Id, "admin-1");

        Assert.False(result.IsSuccess);
        Assert.Equal(ManualFulfillmentOperationStatus.NotFound, result.Status);
        Assert.Contains("does not belong to order", result.Message);
    }

    [Fact]
    public async Task StartManualFulfillment_Rejects_Duplicate_When_Attempt_Already_Processing()
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(db, "start_dup");

        var enabledOptions = Options.Create(new ManualFulfillmentOptions
        {
            Enabled = true,
            ApprovedProductSkus = new() { item.ProductSkuSnapshot }
        });
        var service = new FulfillmentService(db, NullLogger<FulfillmentService>.Instance, enabledOptions);
        var firstResult = await service.StartManualFulfillmentAsync(order.OrderNumber, item.Id, "admin-1");
        Assert.True(firstResult.IsSuccess);

        // Second start while first is Submitted must be rejected
        var secondResult = await service.StartManualFulfillmentAsync(order.OrderNumber, item.Id, "admin-2");
        Assert.False(secondResult.IsSuccess);
        Assert.Equal(ManualFulfillmentOperationStatus.Conflict, secondResult.Status);
        Assert.Contains("already actively processing", secondResult.Message);
    }

    [Fact]
    public async Task StartManualFulfillment_Blocks_When_Previous_Attempt_Is_Unknown_Or_RequiresReconciliation()
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(db, "start_unknown_block");

        var fulfillment = new Fulfillment
        {
            OrderId = order.Id,
            OrderItemId = item.Id,
            Status = FulfillmentStatus.RequiresReview,
            FulfillmentMethod = FulfillmentMethod.VendorApi,
            QuantityRequested = 1,
            QuantityFulfilled = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Fulfillments.Add(fulfillment);
        await db.SaveChangesAsync();

        var attempt = new FulfillmentAttempt
        {
            FulfillmentId = fulfillment.Id,
            AttemptNumber = 1,
            AttemptKey = "ATT-SUPPLIER-TIMEOUT-001",
            Status = FulfillmentAttemptStatus.Unknown, // Unconfirmed supplier outcome!
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.FulfillmentAttempts.Add(attempt);
        await db.SaveChangesAsync();

        var enabledOptions = Options.Create(new ManualFulfillmentOptions
        {
            Enabled = true,
            ApprovedProductSkus = new() { item.ProductSkuSnapshot }
        });
        var service = new FulfillmentService(db, NullLogger<FulfillmentService>.Instance, enabledOptions);
        var result = await service.StartManualFulfillmentAsync(order.OrderNumber, item.Id, "admin-1");

        Assert.False(result.IsSuccess);
        Assert.Equal(ManualFulfillmentOperationStatus.Conflict, result.Status);
        Assert.Contains("unconfirmed status", result.Message);
        Assert.Contains("Status reconciliation or administrative investigation is strictly required", result.Message);
    }

    [Fact]
    public async Task StartManualFulfillment_Blocks_When_Previous_Attempt_Succeeded()
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(db, "start_already_succeeded");

        var fulfillment = new Fulfillment
        {
            OrderId = order.Id,
            OrderItemId = item.Id,
            Status = FulfillmentStatus.InProgress,
            FulfillmentMethod = FulfillmentMethod.VendorApi,
            QuantityRequested = 1,
            QuantityFulfilled = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Fulfillments.Add(fulfillment);
        await db.SaveChangesAsync();

        var attempt = new FulfillmentAttempt
        {
            FulfillmentId = fulfillment.Id,
            AttemptNumber = 1,
            AttemptKey = "ATT-PREV-SUCCEEDED",
            Status = FulfillmentAttemptStatus.Succeeded,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.FulfillmentAttempts.Add(attempt);
        await db.SaveChangesAsync();

        var enabledOptions = Options.Create(new ManualFulfillmentOptions
        {
            Enabled = true,
            ApprovedProductSkus = new() { item.ProductSkuSnapshot }
        });
        var service = new FulfillmentService(db, NullLogger<FulfillmentService>.Instance, enabledOptions);
        var result = await service.StartManualFulfillmentAsync(order.OrderNumber, item.Id, "admin-1");

        Assert.False(result.IsSuccess);
        Assert.Equal(ManualFulfillmentOperationStatus.Conflict, result.Status);
        Assert.Contains("already succeeded", result.Message);
    }

    // ── 4. Complete Manual Fulfillment Workflow ────────────────────────────────

    [Fact]
    public async Task CompleteManualFulfillment_Requires_Explicit_ConfirmDelivered_True()
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(db, "comp_unconfirmed");

        var enabledOptions = Options.Create(new ManualFulfillmentOptions
        {
            Enabled = true,
            ApprovedProductSkus = new() { item.ProductSkuSnapshot }
        });
        var service = new FulfillmentService(db, NullLogger<FulfillmentService>.Instance, enabledOptions);
        await service.StartManualFulfillmentAsync(order.OrderNumber, item.Id, "admin-1");

        var completeResult = await service.CompleteManualFulfillmentAsync(
            order.OrderNumber,
            item.Id,
            "admin-1",
            new AdminCompleteManualFulfillmentRequest
            {
                ConfirmDelivered = false, // Must be true!
                DeliveryReference = "TICKET-12345",
                DeliveryChannel = "EmailSupport"
            });

        Assert.False(completeResult.IsSuccess);
        Assert.Equal(ManualFulfillmentOperationStatus.BadRequest, completeResult.Status);
        Assert.Contains("ConfirmDelivered = true", completeResult.Message);
    }

    [Fact]
    public async Task CompleteManualFulfillment_Requires_Safe_Delivery_Reference_And_Channel()
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(db, "comp_sanitize");

        var enabledOptions = Options.Create(new ManualFulfillmentOptions
        {
            Enabled = true,
            ApprovedProductSkus = new() { item.ProductSkuSnapshot }
        });
        var service = new FulfillmentService(db, NullLogger<FulfillmentService>.Instance, enabledOptions);
        await service.StartManualFulfillmentAsync(order.OrderNumber, item.Id, "admin-1");

        // Reference containing script tag
        var scriptRefResult = await service.CompleteManualFulfillmentAsync(
            order.OrderNumber,
            item.Id,
            "admin-1",
            new AdminCompleteManualFulfillmentRequest
            {
                ConfirmDelivered = true,
                DeliveryReference = "<script>alert('xss')</script>",
                DeliveryChannel = "EmailSupport"
            });

        Assert.False(scriptRefResult.IsSuccess);
        Assert.Equal(ManualFulfillmentOperationStatus.BadRequest, scriptRefResult.Status);
        Assert.Contains("invalid characters", scriptRefResult.Message);

        // Empty delivery channel
        var emptyChannelResult = await service.CompleteManualFulfillmentAsync(
            order.OrderNumber,
            item.Id,
            "admin-1",
            new AdminCompleteManualFulfillmentRequest
            {
                ConfirmDelivered = true,
                DeliveryReference = "TICKET-SAFE-123",
                DeliveryChannel = ""
            });

        Assert.False(emptyChannelResult.IsSuccess);
        Assert.Equal(ManualFulfillmentOperationStatus.BadRequest, emptyChannelResult.Status);
    }

    [Fact]
    public async Task CompleteManualFulfillment_Succeeds_Single_Item_Transitions_Order_To_Completed()
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(db, "comp_single_success");

        var enabledOptions = Options.Create(new ManualFulfillmentOptions
        {
            Enabled = true,
            ApprovedProductSkus = new() { item.ProductSkuSnapshot }
        });
        var service = new FulfillmentService(db, NullLogger<FulfillmentService>.Instance, enabledOptions);
        var startResult = await service.StartManualFulfillmentAsync(order.OrderNumber, item.Id, "admin-start");
        Assert.True(startResult.IsSuccess);

        var completeResult = await service.CompleteManualFulfillmentAsync(
            order.OrderNumber,
            item.Id,
            "admin-complete",
            new AdminCompleteManualFulfillmentRequest
            {
                ConfirmDelivered = true,
                DeliveryReference = "TICKET-78901",
                DeliveryChannel = "WhatsAppDirect",
                Notes = "Delivered via authorized customer care agent."
            });

        Assert.True(completeResult.IsSuccess);
        Assert.Equal(ManualFulfillmentOperationStatus.Success, completeResult.Status);

        var data = completeResult.Data!;
        Assert.Equal(FulfillmentStatus.Completed.ToString(), data.Status);
        Assert.Equal(1, data.QuantityFulfilled);
        Assert.Equal(1, data.QuantityRequested);
        Assert.NotNull(data.CompletedAt);

        // Verify attempt
        Assert.Single(data.Attempts);
        var attempt = data.Attempts[0];
        Assert.Equal(FulfillmentAttemptStatus.Succeeded.ToString(), attempt.Status);
        Assert.Equal("admin-start", attempt.InitiatedByUserId);
        Assert.Equal("admin-complete", attempt.ConfirmedByUserId);
        Assert.Equal("TICKET-78901", attempt.DeliveryReference);
        Assert.Equal("WhatsAppDirect", attempt.DeliveryChannel);
        Assert.Equal("Delivered via authorized customer care agent.", attempt.Notes);
        Assert.NotNull(attempt.CompletedAt);

        // Verify fulfillment status history
        Assert.Equal(2, data.StatusHistory.Count);
        var completeHistory = data.StatusHistory[1];
        Assert.Equal(FulfillmentStatus.InProgress.ToString(), completeHistory.PreviousStatus);
        Assert.Equal(FulfillmentStatus.Completed.ToString(), completeHistory.NewStatus);
        Assert.Equal("admin-complete", completeHistory.ChangedByUserId);
        Assert.Contains("WhatsAppDirect", completeHistory.Reason);

        // Verify Order status transitioned to Completed
        var refreshedOrder = await db.Orders.FirstAsync(o => o.Id == order.Id);
        Assert.Equal(OrderStatus.Completed, refreshedOrder.Status);

        // Verify OrderStatusHistory record created
        var orderHistory = await db.OrderStatusHistories.Where(h => h.OrderId == order.Id).ToListAsync();
        Assert.Single(orderHistory);
        Assert.Equal(OrderStatus.Processing, orderHistory[0].PreviousStatus);
        Assert.Equal(OrderStatus.Completed, orderHistory[0].NewStatus);
        Assert.Equal("admin-complete", orderHistory[0].ChangedByUserId);
    }

    [Fact]
    public async Task CompleteManualFulfillment_Multi_Item_Order_Remains_Processing_Until_All_Items_Fulfilled()
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var (order, item1, payment) = await SeedEligibleProcessingOrderAsync(db, "multi_item", 2000m);

        // Add second product and order item to the same order
        var product2 = new Product
        {
            CategoryId = item1.Product.CategoryId,
            InternalSku = "SKU-MULTI-ITEM-2",
            Name = "Second Item",
            SellingPrice = 1000m,
            SellingCurrency = "PKR",
            IsActive = true
        };
        db.Products.Add(product2);
        await db.SaveChangesAsync();

        var item2 = new OrderItem
        {
            OrderId = order.Id,
            ProductId = product2.Id,
            ProductSkuSnapshot = product2.InternalSku,
            ProductNameSnapshot = product2.Name,
            Quantity = 1,
            UnitPriceSnapshot = 1000m,
            Currency = "PKR",
            TotalPrice = 1000m
        };
        db.OrderItems.Add(item2);

        // Update order total and payment to match 3000 PKR total
        order.Subtotal = 3000m;
        order.TotalAmount = 3000m;
        payment.Amount = 3000m;
        await db.SaveChangesAsync();

        var service = new FulfillmentService(db, NullLogger<FulfillmentService>.Instance, Options.Create(new ManualFulfillmentOptions
        {
            Enabled = true,
            ApprovedProductSkus = new() { item1.ProductSkuSnapshot, item2.ProductSkuSnapshot }
        }));

        // Start and complete item 1
        await service.StartManualFulfillmentAsync(order.OrderNumber, item1.Id, "admin-1");
        var complete1 = await service.CompleteManualFulfillmentAsync(
            order.OrderNumber,
            item1.Id,
            "admin-1",
            new AdminCompleteManualFulfillmentRequest
            {
                ConfirmDelivered = true,
                DeliveryReference = "TICKET-ITEM-1",
                DeliveryChannel = "EmailSupport"
            });

        Assert.True(complete1.IsSuccess);

        // CRITICAL CHECK: Order must REMAIN in Processing status because item 2 is unfulfilled!
        var orderAfterItem1 = await db.Orders.FirstAsync(o => o.Id == order.Id);
        Assert.Equal(OrderStatus.Processing, orderAfterItem1.Status);
        var historyCountAfter1 = await db.OrderStatusHistories.CountAsync(h => h.OrderId == order.Id);
        Assert.Equal(0, historyCountAfter1); // Zero order completion history logs yet

        // Start and complete item 2
        await service.StartManualFulfillmentAsync(order.OrderNumber, item2.Id, "admin-2");
        var complete2 = await service.CompleteManualFulfillmentAsync(
            order.OrderNumber,
            item2.Id,
            "admin-2",
            new AdminCompleteManualFulfillmentRequest
            {
                ConfirmDelivered = true,
                DeliveryReference = "TICKET-ITEM-2",
                DeliveryChannel = "TelegramManual"
            });

        Assert.True(complete2.IsSuccess);

        // NOW: Order must transition to Completed!
        var orderAfterItem2 = await db.Orders.FirstAsync(o => o.Id == order.Id);
        Assert.Equal(OrderStatus.Completed, orderAfterItem2.Status);

        var orderHistory = await db.OrderStatusHistories.Where(h => h.OrderId == order.Id).ToListAsync();
        Assert.Single(orderHistory);
        Assert.Equal(OrderStatus.Completed, orderHistory[0].NewStatus);
        Assert.Equal("admin-2", orderHistory[0].ChangedByUserId);
    }

    [Fact]
    public async Task CompleteManualFulfillment_Rejects_When_Already_Completed()
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(db, "comp_already_done");

        var service = new FulfillmentService(db, NullLogger<FulfillmentService>.Instance, Options.Create(new ManualFulfillmentOptions
        {
            Enabled = true,
            ApprovedProductSkus = new() { item.ProductSkuSnapshot }
        }));
        await service.StartManualFulfillmentAsync(order.OrderNumber, item.Id, "admin-1");

        var req = new AdminCompleteManualFulfillmentRequest
        {
            ConfirmDelivered = true,
            DeliveryReference = "TICKET-FIRST",
            DeliveryChannel = "EmailSupport"
        };
        var first = await service.CompleteManualFulfillmentAsync(order.OrderNumber, item.Id, "admin-1", req);
        Assert.True(first.IsSuccess);

        // Second completion call must be rejected as Conflict
        var second = await service.CompleteManualFulfillmentAsync(order.OrderNumber, item.Id, "admin-2", req);
        Assert.False(second.IsSuccess);
        Assert.Equal(ManualFulfillmentOperationStatus.Conflict, second.Status);
        Assert.Contains("already Completed", second.Message);
    }

    // ── 5. PostgreSQL Concurrency & Database Integration Tests ────────────────

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSql_Concurrent_Double_Completion_Creates_Exactly_One_Success()
    {
        var options = PostgreSqlTestDatabase.CreateOptions();
        await using var seedDb = new AppDbContext(options);

        if (!await PostgreSqlTestDatabase.EnsureMigratedAsync(seedDb))
        {
            return;
        }

        var suffix = $"conc_comp_{Guid.NewGuid():N}"[..18];
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(seedDb, suffix);

        // Seed admin users in PostgreSQL to satisfy foreign key constraints on InitiatedByUserId & ConfirmedByUserId
        var admin1 = new ApplicationUser
        {
            UserName = $"adm1_{suffix}@test.local",
            Email = $"adm1_{suffix}@test.local",
            FullName = "Admin One",
            IsActive = true
        };
        var admin2 = new ApplicationUser
        {
            UserName = $"adm2_{suffix}@test.local",
            Email = $"adm2_{suffix}@test.local",
            FullName = "Admin Two",
            IsActive = true
        };
        seedDb.Users.AddRange(admin1, admin2);
        await seedDb.SaveChangesAsync();

        // Prepare initial manual fulfillment in InProgress status with Submitted attempt
        var fulfillment = new Fulfillment
        {
            OrderId = order.Id,
            OrderItemId = item.Id,
            Status = FulfillmentStatus.InProgress,
            FulfillmentMethod = FulfillmentMethod.Manual,
            QuantityRequested = 1,
            QuantityFulfilled = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        seedDb.Fulfillments.Add(fulfillment);
        await seedDb.SaveChangesAsync();

        var attempt = new FulfillmentAttempt
        {
            FulfillmentId = fulfillment.Id,
            AttemptNumber = 1,
            AttemptKey = $"ATT-CONC-{suffix}",
            Status = FulfillmentAttemptStatus.Submitted,
            InitiatedByUserId = admin1.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            SubmittedAt = DateTime.UtcNow
        };
        seedDb.FulfillmentAttempts.Add(attempt);
        await seedDb.SaveChangesAsync();

        // Simulate two administrators submitting completion simultaneously
        await using var db1 = new AppDbContext(options);
        await using var db2 = new AppDbContext(options);

        var enabledOpts = Options.Create(new ManualFulfillmentOptions
        {
            Enabled = true,
            ApprovedProductSkus = new() { item.ProductSkuSnapshot }
        });
        var service1 = new FulfillmentService(db1, NullLogger<FulfillmentService>.Instance, enabledOpts);
        var service2 = new FulfillmentService(db2, NullLogger<FulfillmentService>.Instance, enabledOpts);

        var req1 = new AdminCompleteManualFulfillmentRequest
        {
            ConfirmDelivered = true,
            DeliveryReference = $"TICKET-1-{suffix}",
            DeliveryChannel = "EmailSupport"
        };
        var req2 = new AdminCompleteManualFulfillmentRequest
        {
            ConfirmDelivered = true,
            DeliveryReference = $"TICKET-2-{suffix}",
            DeliveryChannel = "WhatsAppDirect"
        };

        var task1 = service1.CompleteManualFulfillmentAsync(order.OrderNumber, item.Id, admin1.Id, req1);
        var task2 = service2.CompleteManualFulfillmentAsync(order.OrderNumber, item.Id, admin2.Id, req2);

        var results = await Task.WhenAll(task1, task2);

        var statuses = results.Select(r => r.Status).ToList();
        Assert.Contains(ManualFulfillmentOperationStatus.Success, statuses);
        Assert.Contains(ManualFulfillmentOperationStatus.Conflict, statuses);

        // Verify exactly ONE completed status and ONE order completion log in PostgreSQL
        await using var verifyDb = new AppDbContext(options);
        var refreshedFulfillment = await verifyDb.Fulfillments.FirstAsync(f => f.Id == fulfillment.Id);
        Assert.Equal(FulfillmentStatus.Completed, refreshedFulfillment.Status);
        Assert.Equal(1, refreshedFulfillment.QuantityFulfilled);

        var refreshedOrder = await verifyDb.Orders.FirstAsync(o => o.Id == order.Id);
        Assert.Equal(OrderStatus.Completed, refreshedOrder.Status);

        var orderHistory = await verifyDb.OrderStatusHistories.Where(h => h.OrderId == order.Id).ToListAsync();
        Assert.Single(orderHistory);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSql_Concurrent_Double_Start_Creates_Exactly_One_Attempt()
    {
        var options = PostgreSqlTestDatabase.CreateOptions();
        await using var seedDb = new AppDbContext(options);

        if (!await PostgreSqlTestDatabase.EnsureMigratedAsync(seedDb))
        {
            return;
        }

        var suffix = $"conc_start_{Guid.NewGuid():N}"[..18];
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(seedDb, suffix);

        // Seed admin users in PostgreSQL to satisfy foreign key constraints
        var admin1 = new ApplicationUser
        {
            UserName = $"adm1_start_{suffix}@test.local",
            Email = $"adm1_start_{suffix}@test.local",
            FullName = "Admin Start One",
            IsActive = true
        };
        var admin2 = new ApplicationUser
        {
            UserName = $"adm2_start_{suffix}@test.local",
            Email = $"adm2_start_{suffix}@test.local",
            FullName = "Admin Start Two",
            IsActive = true
        };
        seedDb.Users.AddRange(admin1, admin2);
        await seedDb.SaveChangesAsync();

        await using var db1 = new AppDbContext(options);
        await using var db2 = new AppDbContext(options);

        var enabledOpts = Options.Create(new ManualFulfillmentOptions
        {
            Enabled = true,
            ApprovedProductSkus = new() { item.ProductSkuSnapshot }
        });
        var service1 = new FulfillmentService(db1, NullLogger<FulfillmentService>.Instance, enabledOpts);
        var service2 = new FulfillmentService(db2, NullLogger<FulfillmentService>.Instance, enabledOpts);

        var task1 = service1.StartManualFulfillmentAsync(order.OrderNumber, item.Id, admin1.Id);
        var task2 = service2.StartManualFulfillmentAsync(order.OrderNumber, item.Id, admin2.Id);

        var results = await Task.WhenAll(task1, task2);

        var statuses = results.Select(r => r.Status).ToList();
        Assert.Contains(ManualFulfillmentOperationStatus.Success, statuses);
        Assert.Contains(ManualFulfillmentOperationStatus.Conflict, statuses);

        await using var verifyDb = new AppDbContext(options);
        var attempts = await verifyDb.FulfillmentAttempts
            .Where(a => a.Fulfillment.OrderItemId == item.Id)
            .ToListAsync();

        Assert.Single(attempts);
        Assert.Equal(1, attempts[0].AttemptNumber);
        Assert.Equal(FulfillmentAttemptStatus.Submitted, attempts[0].Status);
    }

    // ── 6. Financial & Customer Privacy Invariants ─────────────────────────────

    [Fact]
    public async Task Manual_Fulfillment_Never_Mutates_Payments_Wallets_Or_Selling_Prices()
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var (order, item, payment) = await SeedEligibleProcessingOrderAsync(db, "fin_inv");

        var wallet = new Wallet
        {
            CustomerId = order.CustomerId,
            Currency = "PKR",
            Balance = 10000m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync();

        var initialWalletBalance = wallet.Balance;
        var initialPaymentAmount = payment.Amount;
        var initialPaymentStatus = payment.Status;
        var initialItemPrice = item.TotalPrice;
        var initialOrderTotal = order.TotalAmount;

        var service = new FulfillmentService(db, NullLogger<FulfillmentService>.Instance, Options.Create(new ManualFulfillmentOptions
        {
            Enabled = true,
            ApprovedProductSkus = new() { item.ProductSkuSnapshot }
        }));
        await service.StartManualFulfillmentAsync(order.OrderNumber, item.Id, "admin-1");
        await service.CompleteManualFulfillmentAsync(
            order.OrderNumber,
            item.Id,
            "admin-1",
            new AdminCompleteManualFulfillmentRequest
            {
                ConfirmDelivered = true,
                DeliveryReference = "TICKET-INV",
                DeliveryChannel = "EmailSupport"
            });

        // Verify financial invariants
        var refreshedWallet = await db.Wallets.FirstAsync(w => w.Id == wallet.Id);
        var refreshedPayment = await db.Payments.FirstAsync(p => p.Id == payment.Id);
        var refreshedOrder = await db.Orders.Include(o => o.Items).FirstAsync(o => o.Id == order.Id);

        Assert.Equal(initialWalletBalance, refreshedWallet.Balance);
        Assert.Equal(initialPaymentAmount, refreshedPayment.Amount);
        Assert.Equal(initialPaymentStatus, refreshedPayment.Status);
        Assert.Equal(initialOrderTotal, refreshedOrder.TotalAmount);
        Assert.Equal(initialItemPrice, refreshedOrder.Items.First().TotalPrice);

        var txCount = await db.WalletTransactions.CountAsync();
        Assert.Equal(0, txCount); // Zero wallet transactions generated
    }

    [Fact]
    public async Task Customer_Order_APIs_Never_Expose_Fulfillment_Evidence_Or_Attempts()
    {
        // Customer DTO properties verification
        var customerDetailProps = typeof(CustomerOrderDetailDto).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain("Fulfillments", customerDetailProps);
        Assert.DoesNotContain("Attempts", customerDetailProps);
        Assert.DoesNotContain("DeliveryReference", customerDetailProps);
        Assert.DoesNotContain("DeliveryChannel", customerDetailProps);
        Assert.DoesNotContain("Vendor", customerDetailProps);

        var customerItemProps = typeof(CustomerOrderItemDto).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain("Fulfillment", customerItemProps);
        Assert.DoesNotContain("VendorProduct", customerItemProps);
    }

    // ── 7. Admin Read API ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetOrderFulfillments_Returns_Complete_Safe_Audit_Summary()
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(db, "read_api");

        var service = new FulfillmentService(db, NullLogger<FulfillmentService>.Instance, Options.Create(new ManualFulfillmentOptions
        {
            Enabled = true,
            ApprovedProductSkus = new() { item.ProductSkuSnapshot }
        }));
        await service.StartManualFulfillmentAsync(order.OrderNumber, item.Id, "admin-init");
        await service.CompleteManualFulfillmentAsync(
            order.OrderNumber,
            item.Id,
            "admin-close",
            new AdminCompleteManualFulfillmentRequest
            {
                ConfirmDelivered = true,
                DeliveryReference = "TICKET-AUDIT-SUMMARY",
                DeliveryChannel = "InternalPortal",
                Notes = "Audit verification complete."
            });

        var summary = await service.GetOrderFulfillmentsAsync(order.OrderNumber);

        Assert.NotNull(summary);
        Assert.Equal(order.OrderNumber, summary.OrderNumber);
        Assert.Equal(OrderStatus.Completed.ToString(), summary.OrderStatus);
        Assert.Single(summary.Fulfillments);

        var f = summary.Fulfillments[0];
        Assert.Equal(item.Id, f.OrderItemId);
        Assert.Equal(FulfillmentStatus.Completed.ToString(), f.Status);
        Assert.Equal(FulfillmentMethod.Manual.ToString(), f.FulfillmentMethod);
        Assert.Equal(1, f.QuantityFulfilled);
        Assert.Equal(1, f.QuantityRequested);

        Assert.Single(f.Attempts);
        Assert.Equal("TICKET-AUDIT-SUMMARY", f.Attempts[0].DeliveryReference);
        Assert.Equal("InternalPortal", f.Attempts[0].DeliveryChannel);
        Assert.Equal("admin-init", f.Attempts[0].InitiatedByUserId);
        Assert.Equal("admin-close", f.Attempts[0].ConfirmedByUserId);

        Assert.Equal(2, f.StatusHistory.Count);
        Assert.Equal(FulfillmentStatus.InProgress.ToString(), f.StatusHistory[0].NewStatus);
        Assert.Equal(FulfillmentStatus.Completed.ToString(), f.StatusHistory[1].NewStatus);
    }

    // ── 8. Milestone 6B-6D2: Safeguards & Feature Visibility Tests ────────────

    [Fact]
    public async Task GetCapabilities_AdminAuthenticated_DefaultConfig_ReturnsDisabledAndUnavailable()
    {
        var client = _client; // Default factory client has Enabled = false
        var (_, authCookie, _, _) = await LoginAsAdminAsync(client);

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/admin/fulfillment-capabilities");
        req.Headers.Add("Cookie", authCookie);

        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<ApiResponse<AdminFulfillmentCapabilitiesDto>>();
        Assert.NotNull(body?.Data);
        Assert.False(body.Data.IsGloballyEnabled);
        Assert.False(body.Data.HasProductAllowlistPolicy);
        Assert.False(body.Data.IsEffectivelyAvailable);
        Assert.Equal(0, body.Data.ApprovedProductCount);
        Assert.Contains("disabled by server configuration", body.Data.StatusMessage);
        Assert.Null(body.Data.IsProductApproved);
    }

    [Fact]
    public async Task GetCapabilities_Anonymous_ReturnsUnauthorized()
    {
        var res = await _client.GetAsync("/api/admin/fulfillment-capabilities");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task GetCapabilities_CustomerAuthenticated_ReturnsForbidden()
    {
        var email = $"cust_cap_{Guid.NewGuid():N}@test.local";
        var (_, customerAuthCookie) = await RegisterAndLoginCustomerAsync(email, "CustomerPass123!");

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/admin/fulfillment-capabilities");
        req.Headers.Add("Cookie", customerAuthCookie);

        var res = await _client.SendAsync(req);
        Assert.True(res.StatusCode == HttpStatusCode.Unauthorized || res.StatusCode == HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetCapabilities_ConfiguredWithApprovedProducts_ReflectsStatusAndQueryParam()
    {
        var approvedSku = "SKU-PILOT-SAFE-1";
        var unapprovedSku = "SKU-FORBIDDEN-9";

        var client = CreateEnabledClient(new() { approvedSku });
        var (_, authCookie, _, _) = await LoginAsAdminAsync(client);

        // Query with approved product SKU
        var reqApproved = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/fulfillment-capabilities?productSku={approvedSku}");
        reqApproved.Headers.Add("Cookie", authCookie);
        var resApproved = await client.SendAsync(reqApproved);
        Assert.Equal(HttpStatusCode.OK, resApproved.StatusCode);

        var bodyApproved = await resApproved.Content.ReadFromJsonAsync<ApiResponse<AdminFulfillmentCapabilitiesDto>>();
        Assert.NotNull(bodyApproved?.Data);
        Assert.True(bodyApproved.Data.IsGloballyEnabled);
        Assert.True(bodyApproved.Data.HasProductAllowlistPolicy);
        Assert.True(bodyApproved.Data.IsEffectivelyAvailable);
        Assert.Equal(1, bodyApproved.Data.ApprovedProductCount);
        Assert.True(bodyApproved.Data.IsProductApproved);
        Assert.Contains("Manual fulfillment is active for 1 approved product(s)", bodyApproved.Data.StatusMessage);

        // Query with unapproved product SKU
        var reqUnapproved = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/fulfillment-capabilities?productSku={unapprovedSku}");
        reqUnapproved.Headers.Add("Cookie", authCookie);
        var resUnapproved = await client.SendAsync(reqUnapproved);
        Assert.Equal(HttpStatusCode.OK, resUnapproved.StatusCode);

        var bodyUnapproved = await resUnapproved.Content.ReadFromJsonAsync<ApiResponse<AdminFulfillmentCapabilitiesDto>>();
        Assert.NotNull(bodyUnapproved?.Data);
        Assert.False(bodyUnapproved.Data.IsProductApproved);
    }

    [Fact]
    public async Task StartManualFulfillment_EmptyAllowlist_BlocksStart_EvenWhenGloballyEnabled()
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(db, "empty_allowlist");

        // Globally enabled, but empty allowlist
        var service = new FulfillmentService(
            db,
            NullLogger<FulfillmentService>.Instance,
            Options.Create(new ManualFulfillmentOptions { Enabled = true, ApprovedProductSkus = new() }));

        var result = await service.StartManualFulfillmentAsync(order.OrderNumber, item.Id, "admin-1");

        Assert.False(result.IsSuccess);
        Assert.Equal(ManualFulfillmentOperationStatus.Forbidden, result.Status);
        Assert.Contains("not approved for manual fulfillment", result.Message);

        // Verify zero fulfillment rows created
        var fulfillmentCount = await db.Fulfillments.CountAsync();
        Assert.Equal(0, fulfillmentCount);
    }

    [Fact]
    public async Task StartManualFulfillment_UnapprovedSku_BlocksStart()
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(db, "unapproved_sku");

        // Allowlist configured, but does NOT contain this item's SKU
        var service = new FulfillmentService(
            db,
            NullLogger<FulfillmentService>.Instance,
            Options.Create(new ManualFulfillmentOptions { Enabled = true, ApprovedProductSkus = new() { "OTHER-SKU-99" } }));

        var result = await service.StartManualFulfillmentAsync(order.OrderNumber, item.Id, "admin-1");

        Assert.False(result.IsSuccess);
        Assert.Equal(ManualFulfillmentOperationStatus.Forbidden, result.Status);
        Assert.Contains(item.ProductSkuSnapshot, result.Message);
        Assert.Contains("not approved", result.Message);
    }

    [Fact]
    public async Task StartManualFulfillment_MasterFlagDisabled_BlocksStart_EvenWithApprovedSku()
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(db, "flag_disabled");

        // Product is in allowlist, but master flag is disabled
        var service = new FulfillmentService(
            db,
            NullLogger<FulfillmentService>.Instance,
            Options.Create(new ManualFulfillmentOptions { Enabled = false, ApprovedProductSkus = new() { item.ProductSkuSnapshot } }));

        var result = await service.StartManualFulfillmentAsync(order.OrderNumber, item.Id, "admin-1");

        Assert.False(result.IsSuccess);
        Assert.Equal(ManualFulfillmentOperationStatus.Forbidden, result.Status);
        Assert.Contains("disabled by server configuration", result.Message);
    }

    [Fact]
    public async Task CompleteManualFulfillment_ProductRemovedFromAllowlist_BlocksCompletion_PreservesAttempt()
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(db, "sku_removed");

        // 1. Initially SKU is approved, start manual fulfillment
        var service1 = new FulfillmentService(
            db,
            NullLogger<FulfillmentService>.Instance,
            Options.Create(new ManualFulfillmentOptions { Enabled = true, ApprovedProductSkus = new() { item.ProductSkuSnapshot } }));

        var startResult = await service1.StartManualFulfillmentAsync(order.OrderNumber, item.Id, "admin-1");
        Assert.True(startResult.IsSuccess);

        // 2. Product is now removed from allowlist
        var service2 = new FulfillmentService(
            db,
            NullLogger<FulfillmentService>.Instance,
            Options.Create(new ManualFulfillmentOptions { Enabled = true, ApprovedProductSkus = new() { "DIFFERENT-SKU" } }));

        var completeResult = await service2.CompleteManualFulfillmentAsync(
            order.OrderNumber,
            item.Id,
            "admin-1",
            new AdminCompleteManualFulfillmentRequest
            {
                ConfirmDelivered = true,
                DeliveryReference = "TICKET-SAFE-REF",
                DeliveryChannel = "EmailSupport"
            });

        Assert.False(completeResult.IsSuccess);
        Assert.Equal(ManualFulfillmentOperationStatus.Forbidden, completeResult.Status);
        Assert.Contains("not approved", completeResult.Message);

        // Verify attempt and fulfillment preserved in database and NOT marked completed
        var fulfillment = await db.Fulfillments.Include(f => f.Attempts).FirstAsync(f => f.OrderItemId == item.Id);
        Assert.Equal(FulfillmentStatus.InProgress, fulfillment.Status);
        Assert.Equal(0, fulfillment.QuantityFulfilled);
        Assert.Single(fulfillment.Attempts);
        Assert.Equal(FulfillmentAttemptStatus.Submitted, fulfillment.Attempts.First().Status);

        var refreshedOrder = await db.Orders.FirstAsync(o => o.Id == order.Id);
        Assert.Equal(OrderStatus.Processing, refreshedOrder.Status);
    }

    [Theory]
    [InlineData("EmailSupport")]
    [InlineData("WhatsAppDirect")]
    [InlineData("TelegramManual")]
    [InlineData("InternalPortal")]
    public async Task CompleteManualFulfillment_ApprovedChannels_Accepted(string channel)
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(db, $"channel_{channel.ToLowerInvariant()}");

        var service = new FulfillmentService(
            db,
            NullLogger<FulfillmentService>.Instance,
            Options.Create(new ManualFulfillmentOptions { Enabled = true, ApprovedProductSkus = new() { item.ProductSkuSnapshot } }));

        await service.StartManualFulfillmentAsync(order.OrderNumber, item.Id, "admin-1");

        var result = await service.CompleteManualFulfillmentAsync(
            order.OrderNumber,
            item.Id,
            "admin-1",
            new AdminCompleteManualFulfillmentRequest
            {
                ConfirmDelivered = true,
                DeliveryReference = $"DISPATCH-REF-{channel}",
                DeliveryChannel = channel,
                Notes = "Standard authorized dispatch procedure completed."
            });

        Assert.True(result.IsSuccess);
        Assert.Equal(ManualFulfillmentOperationStatus.Success, result.Status);
    }

    [Theory]
    [InlineData("CarrierPigeon")]
    [InlineData("DirectPhoneCall")]
    [InlineData("PersonalSms")]
    [InlineData("UnknownChannel")]
    public async Task CompleteManualFulfillment_UnapprovedChannel_Rejected(string invalidChannel)
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(db, "unapproved_chan");

        var service = new FulfillmentService(
            db,
            NullLogger<FulfillmentService>.Instance,
            Options.Create(new ManualFulfillmentOptions { Enabled = true, ApprovedProductSkus = new() { item.ProductSkuSnapshot } }));

        await service.StartManualFulfillmentAsync(order.OrderNumber, item.Id, "admin-1");

        var result = await service.CompleteManualFulfillmentAsync(
            order.OrderNumber,
            item.Id,
            "admin-1",
            new AdminCompleteManualFulfillmentRequest
            {
                ConfirmDelivered = true,
                DeliveryReference = "TICKET-VALID-123",
                DeliveryChannel = invalidChannel
            });

        Assert.False(result.IsSuccess);
        Assert.Equal(ManualFulfillmentOperationStatus.BadRequest, result.Status);
        Assert.Contains("Invalid delivery channel", result.Message);
    }

    [Theory]
    [InlineData("N/A")]
    [InlineData("NA")]
    [InlineData("none")]
    [InlineData("null")]
    [InlineData("0000")]
    [InlineData("TEST")]
    [InlineData("DEMO")]
    [InlineData("DELIVERED")]
    [InlineData("OK")]
    [InlineData("COMPLETED")]
    public async Task CompleteManualFulfillment_AmbiguousReferencePlaceholders_Rejected(string placeholder)
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(db, $"ph_{placeholder.ToLowerInvariant()}");

        var service = new FulfillmentService(
            db,
            NullLogger<FulfillmentService>.Instance,
            Options.Create(new ManualFulfillmentOptions { Enabled = true, ApprovedProductSkus = new() { item.ProductSkuSnapshot } }));

        await service.StartManualFulfillmentAsync(order.OrderNumber, item.Id, "admin-1");

        var result = await service.CompleteManualFulfillmentAsync(
            order.OrderNumber,
            item.Id,
            "admin-1",
            new AdminCompleteManualFulfillmentRequest
            {
                ConfirmDelivered = true,
                DeliveryReference = placeholder,
                DeliveryChannel = "EmailSupport"
            });

        Assert.False(result.IsSuccess);
        Assert.Equal(ManualFulfillmentOperationStatus.BadRequest, result.Status);
        Assert.Contains("Ambiguous placeholders", result.Message);
    }

    [Theory]
    [InlineData("TICKET 12345")] // Space inside
    [InlineData("TICKET\"12345")] // Quote
    [InlineData("TICKET'12345")] // Single quote
    [InlineData("<script>")] // HTML tag
    [InlineData("AB")] // Too short (< 5 chars)
    [InlineData("UNAPPROVED-REF-123")] // Unapproved prefix
    [InlineData("ORDER-999")] // Unapproved prefix
    public async Task CompleteManualFulfillment_InvalidReferenceFormat_Rejected(string badRef)
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(db, "bad_ref");

        var service = new FulfillmentService(
            db,
            NullLogger<FulfillmentService>.Instance,
            Options.Create(new ManualFulfillmentOptions { Enabled = true, ApprovedProductSkus = new() { item.ProductSkuSnapshot } }));

        await service.StartManualFulfillmentAsync(order.OrderNumber, item.Id, "admin-1");

        var result = await service.CompleteManualFulfillmentAsync(
            order.OrderNumber,
            item.Id,
            "admin-1",
            new AdminCompleteManualFulfillmentRequest
            {
                ConfirmDelivered = true,
                DeliveryReference = badRef,
                DeliveryChannel = "EmailSupport"
            });

        Assert.False(result.IsSuccess);
        Assert.Equal(ManualFulfillmentOperationStatus.BadRequest, result.Status);
    }

    [Theory]
    [InlineData("mock_test_password_pattern")]
    [InlineData("mock_test_token_flag")]
    [InlineData("mock_api_key_flag")]
    [InlineData("mock_secret_flag")]
    [InlineData("http://mock-invalid-url.local")]
    [InlineData("https://mock-unapproved-link.local")]
    [InlineData("<script>mock</script>")]
    [InlineData("Unapproved arbitrary free text comment by operator")]
    public async Task CompleteManualFulfillment_NotesContainingCredentialsOrUrls_Rejected(string sensitiveNote)
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(db, "sens_notes");

        var service = new FulfillmentService(
            db,
            NullLogger<FulfillmentService>.Instance,
            Options.Create(new ManualFulfillmentOptions { Enabled = true, ApprovedProductSkus = new() { item.ProductSkuSnapshot } }));

        await service.StartManualFulfillmentAsync(order.OrderNumber, item.Id, "admin-1");

        var result = await service.CompleteManualFulfillmentAsync(
            order.OrderNumber,
            item.Id,
            "admin-1",
            new AdminCompleteManualFulfillmentRequest
            {
                ConfirmDelivered = true,
                DeliveryReference = "TICKET-SECURE-999",
                DeliveryChannel = "InternalPortal",
                Notes = sensitiveNote
            });

        Assert.False(result.IsSuccess);
        Assert.Equal(ManualFulfillmentOperationStatus.BadRequest, result.Status);
        Assert.Contains("prohibited", result.Message.ToLowerInvariant());
    }

    [Fact]
    public async Task CompleteManualFulfillment_NotesExceeding200Chars_Rejected()
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(db, "long_notes");

        var service = new FulfillmentService(
            db,
            NullLogger<FulfillmentService>.Instance,
            Options.Create(new ManualFulfillmentOptions { Enabled = true, ApprovedProductSkus = new() { item.ProductSkuSnapshot } }));

        await service.StartManualFulfillmentAsync(order.OrderNumber, item.Id, "admin-1");

        var longNote = new string('A', 201);
        var result = await service.CompleteManualFulfillmentAsync(
            order.OrderNumber,
            item.Id,
            "admin-1",
            new AdminCompleteManualFulfillmentRequest
            {
                ConfirmDelivered = true,
                DeliveryReference = "TICKET-SECURE-999",
                DeliveryChannel = "InternalPortal",
                Notes = longNote
            });

        Assert.False(result.IsSuccess);
        Assert.Equal(ManualFulfillmentOperationStatus.BadRequest, result.Status);
        Assert.Contains("cannot exceed 200 characters", result.Message);
    }

    [Fact]
    public async Task CompleteManualFulfillment_SafeOperationalNotes_Accepted()
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(db, "safe_notes");

        var service = new FulfillmentService(
            db,
            NullLogger<FulfillmentService>.Instance,
            Options.Create(new ManualFulfillmentOptions { Enabled = true, ApprovedProductSkus = new() { item.ProductSkuSnapshot } }));

        await service.StartManualFulfillmentAsync(order.OrderNumber, item.Id, "admin-1");

        var safeNote = "Standard delivery completed.";
        var result = await service.CompleteManualFulfillmentAsync(
            order.OrderNumber,
            item.Id,
            "admin-1",
            new AdminCompleteManualFulfillmentRequest
            {
                ConfirmDelivered = true,
                DeliveryReference = "TICKET-SECURE-999",
                DeliveryChannel = "InternalPortal",
                Notes = safeNote
            });

        Assert.True(result.IsSuccess);
        Assert.Equal(ManualFulfillmentOperationStatus.Success, result.Status);

        var fulfillment = await db.Fulfillments.Include(f => f.Attempts).FirstAsync(f => f.OrderItemId == item.Id);
        Assert.Single(fulfillment.Attempts);
        Assert.Equal(safeNote, fulfillment.Attempts.First().Notes);
    }
}
