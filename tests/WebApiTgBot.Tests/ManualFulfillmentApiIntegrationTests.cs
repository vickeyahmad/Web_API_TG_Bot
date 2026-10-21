using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApiTgBot.Common.Models;
using WebApiTgBot.Infrastructure.Data;
using WebApiTgBot.Modules.Catalog.Models;
using WebApiTgBot.Modules.Fulfillments.Models;
using WebApiTgBot.Modules.Fulfillments.Models.DTOs;
using WebApiTgBot.Modules.Identity.Controllers;
using WebApiTgBot.Modules.Identity.Models;
using WebApiTgBot.Modules.Identity.Models.DTOs;
using WebApiTgBot.Modules.Orders.Models;
using WebApiTgBot.Modules.Payments.Models;

namespace WebApiTgBot.Tests;

/// <summary>
/// PostgreSQL-backed HTTP integration test suite for Milestone 6B-6E3B2.
/// Prepares automated integration tests for manual fulfillment operations verifying:
/// - Real ASP.NET Core routing and action filter dispatch.
/// - Administrator cookie authentication and role authorization.
/// - Antiforgery cookie and X-XSRF-TOKEN CSRF header validation.
/// - AdminFulfillmentsController dispatch to actual fulfillment services.
/// - Real PostgreSQL row-level locking (SELECT ... FOR UPDATE) and transaction integrity.
/// - Strict defense against double-fulfillment, lost updates, and state discrepancies.
/// - Comprehensive audit logging in OrderStatusHistory and FulfillmentStatusHistory.
///
/// ALL tests in this class are strictly categorized as:
/// [Trait("Category", "PostgreSqlIntegration")]
/// THIS IS A PREPARATION FILE ONLY. Tests must not be executed until live PostgreSQL
/// execution approval is explicitly granted in a subsequent milestone.
/// </summary>
[Trait("Category", "PostgreSqlIntegration")]
public class ManualFulfillmentApiIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlWebApplicationFactory _factory;

    public ManualFulfillmentApiIntegrationTests()
    {
        _factory = new PostgreSqlWebApplicationFactory();
    }

    public async Task InitializeAsync()
    {
        // Fail-closed preflight verification: connects to isolated webapitgbot_test,
        // validates SELECT current_database() == 'webapitgbot_test', and applies migrations.
        // Executed only in live PostgreSQL test runs.
        await _factory.EnsureDatabasePreflightAsync();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // TEST FIXTURE HELPERS & AUTHENTICATION UTILITIES
    // ─────────────────────────────────────────────────────────────────────────────

    private static string NewSuffix() => Guid.NewGuid().ToString("N")[..8];

    private async Task EnsureRolesExistAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        string[] roles = { AppRoles.Admin, AppRoles.Customer };
        foreach (var role in roles)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                await roleManager.CreateAsync(new ApplicationRole(role));
            }
        }
    }

    private async Task<(string AdminId, string AuthCookie, string AntiforgeryCookie, string CsrfToken)> LoginAsAdminAsync(HttpClient client, string? email = null)
    {
        await EnsureRolesExistAsync();
        var adminEmail = email ?? $"admin_test_{NewSuffix()}@test.local".ToLowerInvariant();
        const string password = "SecureAdminPassword123!";
        string adminId;

        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var existing = await userManager.FindByEmailAsync(adminEmail);
            if (existing == null)
            {
                var user = new ApplicationUser
                {
                    UserName = adminEmail,
                    Email = adminEmail,
                    FullName = "Integration Test Admin",
                    EmailConfirmed = true,
                    IsActive = true
                };
                var createRes = await userManager.CreateAsync(user, password);
                Assert.True(createRes.Succeeded, "Admin user creation must succeed.");
                await userManager.AddToRoleAsync(user, AppRoles.Admin);
                adminId = user.Id;
            }
            else
            {
                adminId = existing.Id;
            }
        }

        var loginRes = await client.PostAsJsonAsync("/api/admin/auth/login", new AdminLoginRequest
        {
            Email = adminEmail,
            Password = password
        });
        Assert.Equal(HttpStatusCode.OK, loginRes.StatusCode);

        var authCookie = loginRes.Headers.GetValues("Set-Cookie")
            .First(c => c.Contains(".WebAPI.Admin.Auth"))
            .Split(';')[0];

        var csrfReq = new HttpRequestMessage(HttpMethod.Get, "/api/admin/auth/csrf");
        csrfReq.Headers.Add("Cookie", authCookie);
        var csrfRes = await client.SendAsync(csrfReq);
        Assert.Equal(HttpStatusCode.OK, csrfRes.StatusCode);
        var csrfData = await csrfRes.Content.ReadFromJsonAsync<ApiResponse<string>>();
        Assert.NotNull(csrfData?.Data);

        var antiforgeryCookie = string.Empty;
        if (csrfRes.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            var match = cookies.FirstOrDefault(c => c.Contains(".WebAPI.Antiforgery"));
            if (match != null)
            {
                antiforgeryCookie = match.Split(';')[0];
            }
        }

        return (adminId, authCookie, antiforgeryCookie, csrfData.Data);
    }

    private async Task<(string CustomerId, string AuthCookie)> RegisterAndLoginCustomerAsync(HttpClient client, string email)
    {
        await EnsureRolesExistAsync();
        const string password = "CustomerPassword123!";

        var regRes = await client.PostAsJsonAsync("/api/customer/auth/register", new CustomerRegisterRequest
        {
            Email = email,
            Password = password,
            FullName = "Integration Customer"
        });
        Assert.Equal(HttpStatusCode.Created, regRes.StatusCode);
        var regData = await regRes.Content.ReadFromJsonAsync<ApiResponse<CustomerUserDto>>();

        var loginRes = await client.PostAsJsonAsync("/api/customer/auth/login", new CustomerLoginRequest
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

    private static HttpRequestMessage CreateAdminRequest(
        HttpMethod method,
        string url,
        string authCookie,
        string? antiforgeryCookie = null,
        string? csrfToken = null)
    {
        var request = new HttpRequestMessage(method, url);
        var cookieHeader = authCookie;
        if (!string.IsNullOrEmpty(antiforgeryCookie))
        {
            cookieHeader = $"{cookieHeader}; {antiforgeryCookie}";
        }
        request.Headers.Add("Cookie", cookieHeader);
        if (!string.IsNullOrEmpty(csrfToken))
        {
            request.Headers.Add("X-XSRF-TOKEN", csrfToken);
        }
        return request;
    }

    private static async Task<(Order order, OrderItem item, Payment payment)> SeedEligibleProcessingOrderAsync(
        AppDbContext db,
        string suffix,
        string sku = "SYNTH-APPROVED-1",
        decimal price = 2500m,
        OrderStatus orderStatus = OrderStatus.Processing,
        PaymentStatus paymentStatus = PaymentStatus.Paid,
        decimal paymentAmount = 2500m,
        string currency = "PKR")
    {
        PostgreSqlTestDatabase.AssertMigrated();

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
            InternalSku = sku,
            Name = $"Synthetic Product {suffix}",
            SellingPrice = price,
            SellingCurrency = currency,
            IsActive = true
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();

        var customer = new ApplicationUser
        {
            UserName = $"cust_ord_{suffix}@test.local".ToLowerInvariant(),
            Email = $"cust_ord_{suffix}@test.local".ToLowerInvariant(),
            FullName = $"Customer {suffix}",
            EmailConfirmed = true,
            IsActive = true
        };
        db.Users.Add(customer);
        await db.SaveChangesAsync();

        var order = new Order
        {
            OrderNumber = $"ORD-SYNTH-{suffix}",
            CustomerId = customer.Id,
            Status = orderStatus,
            Currency = currency,
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
            UnitPriceSnapshot = product.SellingPrice,
            Quantity = 1,
            Currency = currency,
            TotalPrice = price
        };
        db.OrderItems.Add(item);

        db.OrderStatusHistories.Add(new OrderStatusHistory
        {
            OrderId = order.Id,
            PreviousStatus = OrderStatus.PendingPayment,
            NewStatus = orderStatus,
            ChangedByUserId = customer.Id,
            Reason = "Integration test order seed"
        });

        var payment = new Payment
        {
            PaymentNumber = $"PAY-SYNTH-{suffix}",
            OrderId = order.Id,
            Amount = paymentAmount,
            Currency = currency,
            PaymentMethodCode = "BANK_TRANSFER",
            Status = paymentStatus,
            Purpose = PaymentPurpose.OrderPurchase,
            IdempotencyKey = $"IDEMP-{suffix}",
            PaidAt = paymentStatus == PaymentStatus.Paid ? DateTime.UtcNow : null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Payments.Add(payment);

        db.PaymentStatusHistories.Add(new PaymentStatusHistory
        {
            PaymentId = payment.Id,
            PreviousStatus = PaymentStatus.Pending,
            NewStatus = paymentStatus,
            ChangedByUserId = customer.Id,
            Reason = "Integration test payment seed"
        });

        await db.SaveChangesAsync();
        return (order, item, payment);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // P1 — SAFETY INTEGRATION TESTS (SCENARIOS 1–11)
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Scenario01_MissingAdministratorAuthentication_Returns401()
    {
        var client = _factory.CreateEnabledClient(new List<string> { "SYNTH-APPROVED-1" });
        var res = await client.PostAsync("/api/admin/orders/ORD-NONEXIST/items/1/manual-fulfillment/start", null);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Scenario02_NonAdminSession_IsRejectedWithForbidden()
    {
        var client = _factory.CreateEnabledClient(new List<string> { "SYNTH-APPROVED-1" });
        var (_, customerAuthCookie) = await RegisterAndLoginCustomerAsync(client, $"cust_{NewSuffix()}@test.local");

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/admin/orders/ORD-NONEXIST/items/1/manual-fulfillment/start");
        req.Headers.Add("Cookie", customerAuthCookie);
        var res = await client.SendAsync(req);

        // Customer role accessing Admin endpoint is rejected with 403 Forbidden
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Scenario03_MissingCsrfToken_RejectsPostWith400()
    {
        var client = _factory.CreateEnabledClient(new List<string> { "SYNTH-APPROVED-1" });
        var (_, authCookie, antiforgeryCookie, _) = await LoginAsAdminAsync(client);

        var req = CreateAdminRequest(
            HttpMethod.Post,
            "/api/admin/orders/ORD-NONEXIST/items/1/manual-fulfillment/start",
            authCookie,
            antiforgeryCookie,
            csrfToken: null); // Missing CSRF token

        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Scenario04_InvalidCsrfToken_RejectsPostWith400()
    {
        var client = _factory.CreateEnabledClient(new List<string> { "SYNTH-APPROVED-1" });
        var (_, authCookie, antiforgeryCookie, _) = await LoginAsAdminAsync(client);

        var req = CreateAdminRequest(
            HttpMethod.Post,
            "/api/admin/orders/ORD-NONEXIST/items/1/manual-fulfillment/start",
            authCookie,
            antiforgeryCookie,
            csrfToken: "INVALID-TAMPERED-CSRF-TOKEN");

        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Scenario05_FeatureDisabledByDefault_BlocksStartWith403()
    {
        // Normal client without feature overrides (Enabled == false)
        var client = _factory.CreateClient();
        var (_, authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync(client);

        var req = CreateAdminRequest(
            HttpMethod.Post,
            "/api/admin/orders/ORD-ANY/items/1/manual-fulfillment/start",
            authCookie,
            antiforgeryCookie,
            csrfToken);

        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Scenario06_EmptyApprovedSkuList_BlocksStartWith403()
    {
        // Enabled == true, but ApprovedProductSkus == []
        var client = _factory.CreateEnabledClient(new List<string>());
        var (_, authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync(client);

        var suffix = NewSuffix();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(db, suffix, sku: "SYNTH-SKU-1");

        var req = CreateAdminRequest(
            HttpMethod.Post,
            $"/api/admin/orders/{order.OrderNumber}/items/{item.Id}/manual-fulfillment/start",
            authCookie,
            antiforgeryCookie,
            csrfToken);

        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Scenario07_UnapprovedProductSku_BlocksStartWith403()
    {
        var client = _factory.CreateEnabledClient(new List<string> { "SYNTH-APPROVED-ONLY" });
        var (_, authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync(client);

        var suffix = NewSuffix();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(db, suffix, sku: "SYNTH-UNAPPROVED-SKU");

        var req = CreateAdminRequest(
            HttpMethod.Post,
            $"/api/admin/orders/{order.OrderNumber}/items/{item.Id}/manual-fulfillment/start",
            authCookie,
            antiforgeryCookie,
            csrfToken);

        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Scenario08_WrongOrderItemId_CannotAffectAnotherItem_ReturnsNotFound()
    {
        var client = _factory.CreateEnabledClient(new List<string> { "SYNTH-APPROVED-1" });
        var (_, authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync(client);

        var suffix = NewSuffix();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(db, suffix, sku: "SYNTH-APPROVED-1");

        var wrongItemId = item.Id + 99999;
        var req = CreateAdminRequest(
            HttpMethod.Post,
            $"/api/admin/orders/{order.OrderNumber}/items/{wrongItemId}/manual-fulfillment/start",
            authCookie,
            antiforgeryCookie,
            csrfToken);

        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Scenario09_UnpaidOrder_CannotStart_ReturnsBadRequest()
    {
        var client = _factory.CreateEnabledClient(new List<string> { "SYNTH-APPROVED-1" });
        var (_, authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync(client);

        var suffix = NewSuffix();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(
            db, suffix, sku: "SYNTH-APPROVED-1",
            orderStatus: OrderStatus.PendingPayment,
            paymentStatus: PaymentStatus.Pending);

        var req = CreateAdminRequest(
            HttpMethod.Post,
            $"/api/admin/orders/{order.OrderNumber}/items/{item.Id}/manual-fulfillment/start",
            authCookie,
            antiforgeryCookie,
            csrfToken);

        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Scenario10_PaidButNonProcessingOrder_CannotStart_ReturnsConflict()
    {
        var client = _factory.CreateEnabledClient(new List<string> { "SYNTH-APPROVED-1" });
        var (_, authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync(client);

        var suffix = NewSuffix();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Order is Paid, but administrator has not transitioned it to Processing
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(
            db, suffix, sku: "SYNTH-APPROVED-1",
            orderStatus: OrderStatus.Paid,
            paymentStatus: PaymentStatus.Paid);

        var req = CreateAdminRequest(
            HttpMethod.Post,
            $"/api/admin/orders/{order.OrderNumber}/items/{item.Id}/manual-fulfillment/start",
            authCookie,
            antiforgeryCookie,
            csrfToken);

        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Scenario11_PaymentAmountOrCurrencyMismatch_BlocksStart_ReturnsBadRequest()
    {
        var client = _factory.CreateEnabledClient(new List<string> { "SYNTH-APPROVED-1" });
        var (_, authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync(client);

        var suffix = NewSuffix();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Order total is 2500m, but paid payment amount is only 1000m
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(
            db, suffix, sku: "SYNTH-APPROVED-1",
            price: 2500m,
            paymentAmount: 1000m);

        var req = CreateAdminRequest(
            HttpMethod.Post,
            $"/api/admin/orders/{order.OrderNumber}/items/{item.Id}/manual-fulfillment/start",
            authCookie,
            antiforgeryCookie,
            csrfToken);

        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // P2 — TRANSACTION INTEGRITY & CONCURRENCY (SCENARIOS 12–23)
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Scenario12_EligibleStart_CreatesOneFulfillmentAndOneAttempt()
    {
        var sku = "SYNTH-APPROVED-1";
        var client = _factory.CreateEnabledClient(new List<string> { sku });
        var (adminId, authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync(client);

        var suffix = NewSuffix();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(db, suffix, sku: sku);

        var req = CreateAdminRequest(
            HttpMethod.Post,
            $"/api/admin/orders/{order.OrderNumber}/items/{item.Id}/manual-fulfillment/start",
            authCookie,
            antiforgeryCookie,
            csrfToken);

        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        // Verify relational persistence
        var fulfillment = await db.Fulfillments
            .Include(f => f.Attempts)
            .Include(f => f.StatusHistory)
            .FirstOrDefaultAsync(f => f.OrderItemId == item.Id);

        Assert.NotNull(fulfillment);
        Assert.Equal(FulfillmentStatus.InProgress, fulfillment.Status);
        Assert.Equal(FulfillmentMethod.Manual, fulfillment.FulfillmentMethod);
        Assert.Single(fulfillment.Attempts);

        var attempt = fulfillment.Attempts.First();
        Assert.Equal(FulfillmentAttemptStatus.Submitted, attempt.Status);
        Assert.Null(attempt.VendorProductId); // Manual attempt has no vendor mapping
        Assert.Equal(adminId, attempt.InitiatedByUserId);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Scenario13_RepeatedStart_CreatesNoDuplicateAttempt_ReturnsConflict()
    {
        var sku = "SYNTH-APPROVED-1";
        var client = _factory.CreateEnabledClient(new List<string> { sku });
        var (_, authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync(client);

        var suffix = NewSuffix();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(db, suffix, sku: sku);

        var req1 = CreateAdminRequest(
            HttpMethod.Post,
            $"/api/admin/orders/{order.OrderNumber}/items/{item.Id}/manual-fulfillment/start",
            authCookie,
            antiforgeryCookie,
            csrfToken);
        var res1 = await client.SendAsync(req1);
        Assert.Equal(HttpStatusCode.OK, res1.StatusCode);

        // Repeated Start on already active manual fulfillment must return 409 Conflict
        var req2 = CreateAdminRequest(
            HttpMethod.Post,
            $"/api/admin/orders/{order.OrderNumber}/items/{item.Id}/manual-fulfillment/start",
            authCookie,
            antiforgeryCookie,
            csrfToken);
        var res2 = await client.SendAsync(req2);
        Assert.Equal(HttpStatusCode.Conflict, res2.StatusCode);

        var attemptsCount = await db.FulfillmentAttempts
            .CountAsync(a => a.Fulfillment.OrderItemId == item.Id);
        Assert.Equal(1, attemptsCount);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Scenario14_ConcurrentStarts_ByTwoAdministrators_SerializeCorrectly()
    {
        var sku = "SYNTH-APPROVED-1";
        var client1 = _factory.CreateEnabledClient(new List<string> { sku });
        var client2 = _factory.CreateEnabledClient(new List<string> { sku });

        var (_, authCookie1, antiforgeryCookie1, csrfToken1) = await LoginAsAdminAsync(client1, $"admin1_{NewSuffix()}@test.local");
        var (_, authCookie2, antiforgeryCookie2, csrfToken2) = await LoginAsAdminAsync(client2, $"admin2_{NewSuffix()}@test.local");

        var suffix = NewSuffix();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(db, suffix, sku: sku);

        var req1 = CreateAdminRequest(
            HttpMethod.Post,
            $"/api/admin/orders/{order.OrderNumber}/items/{item.Id}/manual-fulfillment/start",
            authCookie1,
            antiforgeryCookie1,
            csrfToken1);

        var req2 = CreateAdminRequest(
            HttpMethod.Post,
            $"/api/admin/orders/{order.OrderNumber}/items/{item.Id}/manual-fulfillment/start",
            authCookie2,
            antiforgeryCookie2,
            csrfToken2);

        // Dispatch simultaneously to test real PostgreSQL row locking (SELECT ... FOR UPDATE)
        var task1 = client1.SendAsync(req1);
        var task2 = client2.SendAsync(req2);
        var responses = await Task.WhenAll(task1, task2);

        var statuses = responses.Select(r => r.StatusCode).ToList();
        Assert.Contains(HttpStatusCode.OK, statuses);
        Assert.Contains(HttpStatusCode.Conflict, statuses);

        var attemptsCount = await db.FulfillmentAttempts
            .CountAsync(a => a.Fulfillment.OrderItemId == item.Id);
        Assert.Equal(1, attemptsCount);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Scenario15_Complete_RequiresSubmittedManualAttempt_ReturnsBadRequest()
    {
        var sku = "SYNTH-APPROVED-1";
        var client = _factory.CreateEnabledClient(new List<string> { sku });
        var (_, authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync(client);

        var suffix = NewSuffix();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(db, suffix, sku: sku);

        // Attempting to complete before starting manual fulfillment
        var completeReq = CreateAdminRequest(
            HttpMethod.Post,
            $"/api/admin/orders/{order.OrderNumber}/items/{item.Id}/manual-fulfillment/complete",
            authCookie,
            antiforgeryCookie,
            csrfToken);
        completeReq.Content = JsonContent.Create(new AdminCompleteManualFulfillmentRequest
        {
            ConfirmDelivered = true,
            DeliveryChannel = "EmailSupport",
            DeliveryReference = "TICKET-12345"
        });

        var res = await client.SendAsync(completeReq);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Scenario16_InvalidEvidence_RejectsComplete_ReturnsBadRequest()
    {
        var sku = "SYNTH-APPROVED-1";
        var client = _factory.CreateEnabledClient(new List<string> { sku });
        var (_, authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync(client);

        var suffix = NewSuffix();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(db, suffix, sku: sku);

        var startReq = CreateAdminRequest(
            HttpMethod.Post,
            $"/api/admin/orders/{order.OrderNumber}/items/{item.Id}/manual-fulfillment/start",
            authCookie,
            antiforgeryCookie,
            csrfToken);
        var startRes = await client.SendAsync(startReq);
        Assert.Equal(HttpStatusCode.OK, startRes.StatusCode);

        // DeliveryReference shorter than 3 characters is rejected by validation
        var completeReq = CreateAdminRequest(
            HttpMethod.Post,
            $"/api/admin/orders/{order.OrderNumber}/items/{item.Id}/manual-fulfillment/complete",
            authCookie,
            antiforgeryCookie,
            csrfToken);
        completeReq.Content = JsonContent.Create(new AdminCompleteManualFulfillmentRequest
        {
            ConfirmDelivered = true,
            DeliveryChannel = "EmailSupport",
            DeliveryReference = "A" // Too short (< 3 chars)
        });

        var res = await client.SendAsync(completeReq);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Scenario17_Complete_RequiresConfirmDeliveredTrue_ReturnsBadRequest()
    {
        var sku = "SYNTH-APPROVED-1";
        var client = _factory.CreateEnabledClient(new List<string> { sku });
        var (_, authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync(client);

        var suffix = NewSuffix();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(db, suffix, sku: sku);

        var startReq = CreateAdminRequest(
            HttpMethod.Post,
            $"/api/admin/orders/{order.OrderNumber}/items/{item.Id}/manual-fulfillment/start",
            authCookie,
            antiforgeryCookie,
            csrfToken);
        var startRes = await client.SendAsync(startReq);
        Assert.Equal(HttpStatusCode.OK, startRes.StatusCode);

        var completeReq = CreateAdminRequest(
            HttpMethod.Post,
            $"/api/admin/orders/{order.OrderNumber}/items/{item.Id}/manual-fulfillment/complete",
            authCookie,
            antiforgeryCookie,
            csrfToken);
        completeReq.Content = JsonContent.Create(new AdminCompleteManualFulfillmentRequest
        {
            ConfirmDelivered = false, // Must be true
            DeliveryChannel = "EmailSupport",
            DeliveryReference = "TICKET-SAFE-123"
        });

        var res = await client.SendAsync(completeReq);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Scenario18_SuccessfulComplete_UpdatesFulfillmentAndAttemptHistory()
    {
        var sku = "SYNTH-APPROVED-1";
        var client = _factory.CreateEnabledClient(new List<string> { sku });
        var (_, authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync(client);

        var suffix = NewSuffix();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(db, suffix, sku: sku);

        var startReq = CreateAdminRequest(
            HttpMethod.Post,
            $"/api/admin/orders/{order.OrderNumber}/items/{item.Id}/manual-fulfillment/start",
            authCookie,
            antiforgeryCookie,
            csrfToken);
        var startRes = await client.SendAsync(startReq);
        Assert.Equal(HttpStatusCode.OK, startRes.StatusCode);

        var completeReq = CreateAdminRequest(
            HttpMethod.Post,
            $"/api/admin/orders/{order.OrderNumber}/items/{item.Id}/manual-fulfillment/complete",
            authCookie,
            antiforgeryCookie,
            csrfToken);
        completeReq.Content = JsonContent.Create(new AdminCompleteManualFulfillmentRequest
        {
            ConfirmDelivered = true,
            DeliveryChannel = "EmailSupport",
            DeliveryReference = "TICKET-DELIVERED-99"
        });

        var completeRes = await client.SendAsync(completeReq);
        Assert.Equal(HttpStatusCode.OK, completeRes.StatusCode);

        // Verify relational state
        var fulfillment = await db.Fulfillments
            .Include(f => f.Attempts)
            .Include(f => f.StatusHistory)
            .FirstAsync(f => f.OrderItemId == item.Id);

        Assert.Equal(FulfillmentStatus.Completed, fulfillment.Status);
        Assert.Equal(item.Quantity, fulfillment.QuantityFulfilled);

        var attempt = fulfillment.Attempts.First();
        Assert.Equal(FulfillmentAttemptStatus.Succeeded, attempt.Status);

        Assert.Contains(fulfillment.StatusHistory, h =>
            h.PreviousStatus == FulfillmentStatus.InProgress && h.NewStatus == FulfillmentStatus.Completed);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Scenario19_RepeatedComplete_CreatesNoDuplicateCompletion_ReturnsConflict()
    {
        var sku = "SYNTH-APPROVED-1";
        var client = _factory.CreateEnabledClient(new List<string> { sku });
        var (_, authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync(client);

        var suffix = NewSuffix();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(db, suffix, sku: sku);

        var startReq = CreateAdminRequest(
            HttpMethod.Post,
            $"/api/admin/orders/{order.OrderNumber}/items/{item.Id}/manual-fulfillment/start",
            authCookie,
            antiforgeryCookie,
            csrfToken);
        await client.SendAsync(startReq);

        var completePayload = new AdminCompleteManualFulfillmentRequest
        {
            ConfirmDelivered = true,
            DeliveryChannel = "EmailSupport",
            DeliveryReference = "TICKET-ONCE-123"
        };

        var req1 = CreateAdminRequest(
            HttpMethod.Post,
            $"/api/admin/orders/{order.OrderNumber}/items/{item.Id}/manual-fulfillment/complete",
            authCookie,
            antiforgeryCookie,
            csrfToken);
        req1.Content = JsonContent.Create(completePayload);
        var res1 = await client.SendAsync(req1);
        Assert.Equal(HttpStatusCode.OK, res1.StatusCode);

        var req2 = CreateAdminRequest(
            HttpMethod.Post,
            $"/api/admin/orders/{order.OrderNumber}/items/{item.Id}/manual-fulfillment/complete",
            authCookie,
            antiforgeryCookie,
            csrfToken);
        req2.Content = JsonContent.Create(completePayload);
        var res2 = await client.SendAsync(req2);
        Assert.Equal(HttpStatusCode.Conflict, res2.StatusCode);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Scenario20_ConcurrentCompletes_CreateExactlyOneSuccessfulResult()
    {
        var sku = "SYNTH-APPROVED-1";
        var client1 = _factory.CreateEnabledClient(new List<string> { sku });
        var client2 = _factory.CreateEnabledClient(new List<string> { sku });

        var (_, authCookie1, antiforgeryCookie1, csrfToken1) = await LoginAsAdminAsync(client1, $"adminA_{NewSuffix()}@test.local");
        var (_, authCookie2, antiforgeryCookie2, csrfToken2) = await LoginAsAdminAsync(client2, $"adminB_{NewSuffix()}@test.local");

        var suffix = NewSuffix();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(db, suffix, sku: sku);

        var startReq = CreateAdminRequest(
            HttpMethod.Post,
            $"/api/admin/orders/{order.OrderNumber}/items/{item.Id}/manual-fulfillment/start",
            authCookie1,
            antiforgeryCookie1,
            csrfToken1);
        var startRes = await client1.SendAsync(startReq);
        Assert.Equal(HttpStatusCode.OK, startRes.StatusCode);

        var payload1 = new AdminCompleteManualFulfillmentRequest
        {
            ConfirmDelivered = true,
            DeliveryChannel = "EmailSupport",
            DeliveryReference = "TICKET-CONC-A"
        };
        var payload2 = new AdminCompleteManualFulfillmentRequest
        {
            ConfirmDelivered = true,
            DeliveryChannel = "EmailSupport",
            DeliveryReference = "TICKET-CONC-B"
        };

        var req1 = CreateAdminRequest(
            HttpMethod.Post,
            $"/api/admin/orders/{order.OrderNumber}/items/{item.Id}/manual-fulfillment/complete",
            authCookie1,
            antiforgeryCookie1,
            csrfToken1);
        req1.Content = JsonContent.Create(payload1);

        var req2 = CreateAdminRequest(
            HttpMethod.Post,
            $"/api/admin/orders/{order.OrderNumber}/items/{item.Id}/manual-fulfillment/complete",
            authCookie2,
            antiforgeryCookie2,
            csrfToken2);
        req2.Content = JsonContent.Create(payload2);

        var task1 = client1.SendAsync(req1);
        var task2 = client2.SendAsync(req2);
        var responses = await Task.WhenAll(task1, task2);

        var statuses = responses.Select(r => r.StatusCode).ToList();
        Assert.Contains(HttpStatusCode.OK, statuses);
        Assert.Contains(HttpStatusCode.Conflict, statuses);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Scenario21_CompletingOneItem_LeavesParentOrderProcessing()
    {
        var sku = "SYNTH-APPROVED-1";
        var client = _factory.CreateEnabledClient(new List<string> { sku });
        var (_, authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync(client);

        var suffix = NewSuffix();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Seed order with 2 items
        var (order, item1, _) = await SeedEligibleProcessingOrderAsync(db, suffix, sku: sku, price: 5000m, paymentAmount: 5000m);

        var product2 = new Product
        {
            CategoryId = (await db.Categories.FirstAsync()).Id,
            InternalSku = $"{sku}-B",
            Name = $"Synthetic Product B {suffix}",
            SellingPrice = 2500m,
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
            UnitPriceSnapshot = product2.SellingPrice,
            Quantity = 1,
            Currency = "PKR",
            TotalPrice = 2500m
        };
        db.OrderItems.Add(item2);
        await db.SaveChangesAsync();

        // Start and complete item 1
        var startReq = CreateAdminRequest(
            HttpMethod.Post,
            $"/api/admin/orders/{order.OrderNumber}/items/{item1.Id}/manual-fulfillment/start",
            authCookie,
            antiforgeryCookie,
            csrfToken);
        await client.SendAsync(startReq);

        var completeReq = CreateAdminRequest(
            HttpMethod.Post,
            $"/api/admin/orders/{order.OrderNumber}/items/{item1.Id}/manual-fulfillment/complete",
            authCookie,
            antiforgeryCookie,
            csrfToken);
        completeReq.Content = JsonContent.Create(new AdminCompleteManualFulfillmentRequest
        {
            ConfirmDelivered = true,
            DeliveryChannel = "EmailSupport",
            DeliveryReference = "TICKET-ITEM1-ONLY"
        });
        var completeRes = await client.SendAsync(completeReq);
        Assert.Equal(HttpStatusCode.OK, completeRes.StatusCode);

        // Parent order must remain Processing because item 2 is not yet fulfilled
        var updatedOrder = await db.Orders.FindAsync(order.Id);
        Assert.NotNull(updatedOrder);
        Assert.Equal(OrderStatus.Processing, updatedOrder.Status);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Scenario22_CompletingFinalItem_CompletesParentOrder()
    {
        var sku = "SYNTH-APPROVED-1";
        var client = _factory.CreateEnabledClient(new List<string> { sku });
        var (_, authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync(client);

        var suffix = NewSuffix();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(db, suffix, sku: sku);

        var startReq = CreateAdminRequest(
            HttpMethod.Post,
            $"/api/admin/orders/{order.OrderNumber}/items/{item.Id}/manual-fulfillment/start",
            authCookie,
            antiforgeryCookie,
            csrfToken);
        await client.SendAsync(startReq);

        var completeReq = CreateAdminRequest(
            HttpMethod.Post,
            $"/api/admin/orders/{order.OrderNumber}/items/{item.Id}/manual-fulfillment/complete",
            authCookie,
            antiforgeryCookie,
            csrfToken);
        completeReq.Content = JsonContent.Create(new AdminCompleteManualFulfillmentRequest
        {
            ConfirmDelivered = true,
            DeliveryChannel = "EmailSupport",
            DeliveryReference = "TICKET-FINAL-ITEM"
        });
        var completeRes = await client.SendAsync(completeReq);
        Assert.Equal(HttpStatusCode.OK, completeRes.StatusCode);

        // Final item completed: parent order must transition to Completed
        var completedOrder = await db.Orders
            .Include(o => o.StatusHistory)
            .FirstAsync(o => o.Id == order.Id);

        Assert.Equal(OrderStatus.Completed, completedOrder.Status);
        Assert.Contains(completedOrder.StatusHistory, h =>
            h.PreviousStatus == OrderStatus.Processing && h.NewStatus == OrderStatus.Completed);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Scenario23_FailedMutations_DoNotLeaveOrphanedAttemptsOrPartialHistory()
    {
        var sku = "SYNTH-APPROVED-1";
        var client = _factory.CreateEnabledClient(new List<string> { sku });
        var (_, authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync(client);

        var suffix = NewSuffix();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(db, suffix, sku: sku);

        // Issue invalid start request (tampered route param)
        var badReq = CreateAdminRequest(
            HttpMethod.Post,
            $"/api/admin/orders/{order.OrderNumber}/items/999999/manual-fulfillment/start",
            authCookie,
            antiforgeryCookie,
            csrfToken);

        var res = await client.SendAsync(badReq);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);

        // Transaction atomicity: no fulfillment attempts or status histories created
        var anyAttempts = await db.FulfillmentAttempts
            .AnyAsync(a => a.Fulfillment.OrderItemId == item.Id);
        Assert.False(anyAttempts);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // P3 — OPERATIONAL RECOVERY & SYSTEM INVARIANTS (SCENARIOS 24–30)
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Scenario24_UnknownOrRequiresReconciliationAttempts_BlockFurtherActions()
    {
        var sku = "SYNTH-APPROVED-1";
        var client = _factory.CreateEnabledClient(new List<string> { sku });
        var (_, authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync(client);

        var suffix = NewSuffix();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(db, suffix, sku: sku);

        // Seed an existing attempt in RequiresReconciliation state
        var fulfillment = new Fulfillment
        {
            OrderId = order.Id,
            OrderItemId = item.Id,
            FulfillmentMethod = FulfillmentMethod.Manual,
            Status = FulfillmentStatus.RequiresReview,
            QuantityRequested = item.Quantity,
            QuantityFulfilled = 0
        };
        db.Fulfillments.Add(fulfillment);
        await db.SaveChangesAsync();

        db.FulfillmentAttempts.Add(new FulfillmentAttempt
        {
            FulfillmentId = fulfillment.Id,
            Status = FulfillmentAttemptStatus.RequiresReconciliation,
            AttemptKey = $"ATT-REC-{suffix}",
            AttemptNumber = 1,
            SafeErrorSummary = "Reconciliation required"
        });
        await db.SaveChangesAsync();

        var req = CreateAdminRequest(
            HttpMethod.Post,
            $"/api/admin/orders/{order.OrderNumber}/items/{item.Id}/manual-fulfillment/start",
            authCookie,
            antiforgeryCookie,
            csrfToken);

        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Scenario25_SkuApprovalRevokedDuringAttempt_BlocksCompletion()
    {
        var sku = "SYNTH-REVOKED-SKU";
        // Start client with SKU approved
        var startClient = _factory.CreateEnabledClient(new List<string> { sku });
        var (_, authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync(startClient);

        var suffix = NewSuffix();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(db, suffix, sku: sku);

        var startReq = CreateAdminRequest(
            HttpMethod.Post,
            $"/api/admin/orders/{order.OrderNumber}/items/{item.Id}/manual-fulfillment/start",
            authCookie,
            antiforgeryCookie,
            csrfToken);
        var startRes = await startClient.SendAsync(startReq);
        Assert.Equal(HttpStatusCode.OK, startRes.StatusCode);

        // Second client where SKU approval was revoked (empty approved list)
        var revokedClient = _factory.CreateEnabledClient(new List<string>());
        var (_, authCookieRev, antiforgeryCookieRev, csrfTokenRev) = await LoginAsAdminAsync(revokedClient);

        var completeReq = CreateAdminRequest(
            HttpMethod.Post,
            $"/api/admin/orders/{order.OrderNumber}/items/{item.Id}/manual-fulfillment/complete",
            authCookieRev,
            antiforgeryCookieRev,
            csrfTokenRev);
        completeReq.Content = JsonContent.Create(new AdminCompleteManualFulfillmentRequest
        {
            ConfirmDelivered = true,
            DeliveryChannel = "EmailSupport",
            DeliveryReference = "TICKET-REVOKED-SKU"
        });

        var res = await revokedClient.SendAsync(completeReq);
        // Revoked SKU blocks completion with 403 Forbidden
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Scenario26_GlobalFeatureDisabledDuringAttempt_BlocksCompletion()
    {
        var sku = "SYNTH-APPROVED-1";
        var startClient = _factory.CreateEnabledClient(new List<string> { sku });
        var (_, authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync(startClient);

        var suffix = NewSuffix();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(db, suffix, sku: sku);

        var startReq = CreateAdminRequest(
            HttpMethod.Post,
            $"/api/admin/orders/{order.OrderNumber}/items/{item.Id}/manual-fulfillment/start",
            authCookie,
            antiforgeryCookie,
            csrfToken);
        var startRes = await startClient.SendAsync(startReq);
        Assert.Equal(HttpStatusCode.OK, startRes.StatusCode);

        // Feature disabled client (normal defaults)
        var defaultClient = _factory.CreateClient();
        var (_, authCookieDef, antiforgeryCookieDef, csrfTokenDef) = await LoginAsAdminAsync(defaultClient);

        var completeReq = CreateAdminRequest(
            HttpMethod.Post,
            $"/api/admin/orders/{order.OrderNumber}/items/{item.Id}/manual-fulfillment/complete",
            authCookieDef,
            antiforgeryCookieDef,
            csrfTokenDef);
        completeReq.Content = JsonContent.Create(new AdminCompleteManualFulfillmentRequest
        {
            ConfirmDelivered = true,
            DeliveryChannel = "EmailSupport",
            DeliveryReference = "TICKET-DISABLED-FEATURE"
        });

        var res = await defaultClient.SendAsync(completeReq);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Scenario27_LostHttpResponse_FollowedByGet_DoesNotCreateAnotherAttempt()
    {
        var sku = "SYNTH-APPROVED-1";
        var client = _factory.CreateEnabledClient(new List<string> { sku });
        var (_, authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync(client);

        var suffix = NewSuffix();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(db, suffix, sku: sku);

        var startReq = CreateAdminRequest(
            HttpMethod.Post,
            $"/api/admin/orders/{order.OrderNumber}/items/{item.Id}/manual-fulfillment/start",
            authCookie,
            antiforgeryCookie,
            csrfToken);

        // Simulate request dispatch where client discards/loses response stream
        using (var response = await client.SendAsync(startReq, HttpCompletionOption.ResponseHeadersRead))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            // Simulate client dropping connection before reading full body
        }

        // Operational recovery: Client reconciles state by querying GET order fulfillments
        var getReq = CreateAdminRequest(
            HttpMethod.Get,
            $"/api/admin/orders/{order.OrderNumber}/fulfillments",
            authCookie);
        var getRes = await client.SendAsync(getReq);
        Assert.Equal(HttpStatusCode.OK, getRes.StatusCode);

        var data = await getRes.Content.ReadFromJsonAsync<ApiResponse<AdminOrderFulfillmentsDto>>();
        Assert.NotNull(data?.Data);
        Assert.Single(data.Data.Fulfillments);

        // Verification: Exactly one attempt exists despite lost response recovery
        var attemptsCount = await db.FulfillmentAttempts
            .CountAsync(a => a.Fulfillment.OrderItemId == item.Id);
        Assert.Equal(1, attemptsCount);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Scenario28_FinancialWalletAndLedgerState_RemainUnchanged()
    {
        var sku = "SYNTH-APPROVED-1";
        var client = _factory.CreateEnabledClient(new List<string> { sku });
        var (_, authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync(client);

        var suffix = NewSuffix();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(db, suffix, sku: sku);

        var initialWalletCount = await db.Wallets.CountAsync();
        var initialTxCount = await db.WalletTransactions.CountAsync();

        var startReq = CreateAdminRequest(
            HttpMethod.Post,
            $"/api/admin/orders/{order.OrderNumber}/items/{item.Id}/manual-fulfillment/start",
            authCookie,
            antiforgeryCookie,
            csrfToken);
        await client.SendAsync(startReq);

        var completeReq = CreateAdminRequest(
            HttpMethod.Post,
            $"/api/admin/orders/{order.OrderNumber}/items/{item.Id}/manual-fulfillment/complete",
            authCookie,
            antiforgeryCookie,
            csrfToken);
        completeReq.Content = JsonContent.Create(new AdminCompleteManualFulfillmentRequest
        {
            ConfirmDelivered = true,
            DeliveryChannel = "EmailSupport",
            DeliveryReference = "TICKET-WALLET-TEST"
        });
        await client.SendAsync(completeReq);

        // Verification: Financial ledger state is never mutated by fulfillment operations
        var finalWalletCount = await db.Wallets.CountAsync();
        var finalTxCount = await db.WalletTransactions.CountAsync();
        Assert.Equal(initialWalletCount, finalWalletCount);
        Assert.Equal(initialTxCount, finalTxCount);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Scenario29_VendorOrderDispatch_RemainsAbsentForManualFulfillment()
    {
        var sku = "SYNTH-APPROVED-1";
        var client = _factory.CreateEnabledClient(new List<string> { sku });
        var (_, authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync(client);

        var suffix = NewSuffix();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(db, suffix, sku: sku);

        var startReq = CreateAdminRequest(
            HttpMethod.Post,
            $"/api/admin/orders/{order.OrderNumber}/items/{item.Id}/manual-fulfillment/start",
            authCookie,
            antiforgeryCookie,
            csrfToken);
        await client.SendAsync(startReq);

        // Verification: Manual fulfillment attempt records VendorProductId as NULL
        // Zero vendor orders dispatched; zero supplier API credentials referenced
        var attempt = await db.FulfillmentAttempts
            .FirstAsync(a => a.Fulfillment.OrderItemId == item.Id);

        Assert.Null(attempt.VendorProductId);
        Assert.Null(attempt.ExternalVendorOrderId);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Scenario30_AuditLogsAndStatusHistories_AreCorrect()
    {
        var sku = "SYNTH-APPROVED-1";
        var client = _factory.CreateEnabledClient(new List<string> { sku });
        var (adminId, authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync(client);

        var suffix = NewSuffix();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (order, item, _) = await SeedEligibleProcessingOrderAsync(db, suffix, sku: sku);

        var startReq = CreateAdminRequest(
            HttpMethod.Post,
            $"/api/admin/orders/{order.OrderNumber}/items/{item.Id}/manual-fulfillment/start",
            authCookie,
            antiforgeryCookie,
            csrfToken);
        await client.SendAsync(startReq);

        var completeReq = CreateAdminRequest(
            HttpMethod.Post,
            $"/api/admin/orders/{order.OrderNumber}/items/{item.Id}/manual-fulfillment/complete",
            authCookie,
            antiforgeryCookie,
            csrfToken);
        completeReq.Content = JsonContent.Create(new AdminCompleteManualFulfillmentRequest
        {
            ConfirmDelivered = true,
            DeliveryChannel = "EmailSupport",
            DeliveryReference = "TICKET-AUDIT-30"
        });
        await client.SendAsync(completeReq);

        // Verify FulfillmentStatusHistory audit trail
        var fulfillment = await db.Fulfillments
            .Include(f => f.StatusHistory)
            .FirstAsync(f => f.OrderItemId == item.Id);

        var histories = fulfillment.StatusHistory.OrderBy(h => h.ChangedAt).ToList();
        Assert.True(histories.Count >= 2);

        var startHistory = histories.First();
        Assert.Equal(FulfillmentStatus.Pending, startHistory.PreviousStatus);
        Assert.Equal(FulfillmentStatus.InProgress, startHistory.NewStatus);
        Assert.Equal(adminId, startHistory.ChangedByUserId);

        var completeHistory = histories.Last();
        Assert.Equal(FulfillmentStatus.InProgress, completeHistory.PreviousStatus);
        Assert.Equal(FulfillmentStatus.Completed, completeHistory.NewStatus);
        Assert.Equal(adminId, completeHistory.ChangedByUserId);
    }
}
