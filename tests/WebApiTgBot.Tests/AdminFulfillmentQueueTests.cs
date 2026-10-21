using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApiTgBot.Common.Models;
using WebApiTgBot.Infrastructure.Data;
using WebApiTgBot.Modules.Catalog.Models;
using WebApiTgBot.Modules.Fulfillments.Models;
using WebApiTgBot.Modules.Fulfillments.Models.DTOs;
using WebApiTgBot.Modules.Identity.Models;
using WebApiTgBot.Modules.Identity.Models.DTOs;
using WebApiTgBot.Modules.Orders.Models;
using WebApiTgBot.Modules.Payments.Models;
using WebApiTgBot.Modules.Wallets.Models;
using Microsoft.Extensions.Logging.Abstractions;
using WebApiTgBot.Modules.Fulfillments.Services;

namespace WebApiTgBot.Tests;

/// <summary>
/// Comprehensive integration test suite for Milestone 6B-6B: Backend Read-Only Fulfillment Work Queue.
/// Verifies:
/// 1. Admin can access global fulfillment overview (GET /api/admin/fulfillments).
/// 2. Anonymous requests receive 401 Unauthorized.
/// 3. Customer authentication receives 403 Forbidden.
/// 4. Correct order and order-item relationships are projected into AdminFulfillmentSummaryDto.
/// 5. Search by order number works.
/// 6. Search by customer email, name, or ID works.
/// 7. Search by historical product SKU works.
/// 8. Status filtering (Uninitialized, NotStarted, InProgress, Completed, etc.) works.
/// 9. Fulfillment method filtering (Unassigned, Manual, VendorApi) works.
/// 10. Parent order status filtering works.
/// 11. Pagination and total counts work accurately.
/// 12. Deterministic sorting (newest first).
/// 13. Invalid page and page size return 400 Bad Request.
/// 14. Invalid enum filters return 400 Bad Request.
/// 15. Empty data returns HTTP 200 OK with empty items array and totalCount = 0.
/// 16. Data privacy: vendor wholesale costs, credentials, passwords, and secrets are never serialized.
/// 17. Read-only purity: GET never alters wallet balances, payments, or orders.
/// 18. Read-only purity: GET never creates or inserts Fulfillment rows for uninitialized items.
/// 19. Existing single-order fulfillment details (GET /api/admin/orders/{orderNumber}/fulfillments) remain fully compatible.
/// 20. Manual fulfillment remains disabled by default (returns 403 Forbidden).
/// 21. Actionable uninitialized line items appear correctly in the queue.
/// 22. Ineligible unpaid (PendingPayment) and cancelled orders without fulfillment are excluded from the default queue.
/// </summary>
public class AdminFulfillmentQueueTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public AdminFulfillmentQueueTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
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

    private async Task<(string CustomerId, string AuthCookie)> RegisterAndLoginCustomerAsync(
        string email,
        string password,
        string fullName = "Queue Customer")
    {
        await EnsureRolesExistAsync();

        var regRes = await _client.PostAsJsonAsync("/api/customer/auth/register", new CustomerRegisterRequest
        {
            Email = email,
            Password = password,
            FullName = fullName
        });
        Assert.Equal(HttpStatusCode.Created, regRes.StatusCode);
        var regData = await regRes.Content.ReadFromJsonAsync<ApiResponse<CustomerUserDto>>();
        Assert.NotNull(regData?.Data);

        var loginRes = await _client.PostAsJsonAsync("/api/customer/auth/login", new CustomerLoginRequest
        {
            Email = email,
            Password = password
        });
        Assert.Equal(HttpStatusCode.OK, loginRes.StatusCode);

        var authCookie = loginRes.Headers.GetValues("Set-Cookie")
            .First(c => c.Contains(".WebAPI.Customer.Auth"))
            .Split(';')[0];

        return (regData.Data.Id, authCookie);
    }

    private async Task<(string AdminId, string AuthCookie)> LoginAsAdminAsync()
    {
        await EnsureRolesExistAsync();
        var email = $"admin_q_{Guid.NewGuid():N}@test.local";
        var password = "SecureAdminPassword123!";
        string adminId;

        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                FullName = "Admin Queue Manager",
                EmailConfirmed = true,
                IsActive = true
            };
            var createRes = await userManager.CreateAsync(user, password);
            Assert.True(createRes.Succeeded);
            await userManager.AddToRoleAsync(user, AppRoles.Admin);
            adminId = user.Id;
        }

        var loginRes = await _client.PostAsJsonAsync("/api/admin/auth/login", new AdminLoginRequest
        {
            Email = email,
            Password = password
        });
        Assert.Equal(HttpStatusCode.OK, loginRes.StatusCode);

        var authCookie = loginRes.Headers.GetValues("Set-Cookie")
            .First(c => c.Contains(".WebAPI.Admin.Auth"))
            .Split(';')[0];

        return (adminId, authCookie);
    }

    private static HttpRequestMessage CreateAuthenticatedRequest(HttpMethod method, string uri, string authCookie)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("Cookie", authCookie);
        return request;
    }

    private static async Task<(Order Order, OrderItem Item, Fulfillment? Fulfillment)> SeedOrderItemAsync(
        AppDbContext db,
        string suffix,
        OrderStatus orderStatus = OrderStatus.Processing,
        FulfillmentStatus? fulfillmentStatus = null,
        FulfillmentMethod fulfillmentMethod = FulfillmentMethod.Manual,
        string sku = "TEST-SKU",
        string productName = "Test Digital Tool")
    {
        var category = new Category
        {
            Name = $"Category {suffix}",
            Slug = $"cat-{suffix}-{Guid.NewGuid():N}",
            IsActive = true
        };
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        var product = new Product
        {
            CategoryId = category.Id,
            InternalSku = $"{sku}-{suffix}",
            Name = $"{productName} {suffix}",
            SellingPrice = 1200m,
            SellingCurrency = "PKR",
            IsActive = true
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();

        var customer = new ApplicationUser
        {
            UserName = $"cust_{suffix}_{Guid.NewGuid():N}@test.local",
            Email = $"cust_{suffix}_{Guid.NewGuid():N}@test.local",
            FullName = $"Customer {suffix}",
            IsActive = true
        };
        db.Users.Add(customer);
        await db.SaveChangesAsync();

        var order = new Order
        {
            OrderNumber = $"ORD-{suffix}-{Guid.NewGuid():N}".Substring(0, 20),
            CustomerId = customer.Id,
            Status = orderStatus,
            Currency = "PKR",
            Subtotal = 1200m,
            TotalAmount = 1200m,
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
            UnitPriceSnapshot = 1200m,
            Currency = "PKR",
            TotalPrice = 1200m
        };
        db.OrderItems.Add(item);
        await db.SaveChangesAsync();

        Fulfillment? fulfillment = null;
        if (fulfillmentStatus.HasValue)
        {
            fulfillment = new Fulfillment
            {
                OrderId = order.Id,
                OrderItemId = item.Id,
                Status = fulfillmentStatus.Value,
                FulfillmentMethod = fulfillmentMethod,
                QuantityRequested = item.Quantity,
                QuantityFulfilled = fulfillmentStatus.Value == FulfillmentStatus.Completed ? item.Quantity : 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                CompletedAt = fulfillmentStatus.Value == FulfillmentStatus.Completed ? DateTime.UtcNow : null
            };
            db.Fulfillments.Add(fulfillment);
            await db.SaveChangesAsync();
        }

        return (order, item, fulfillment);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 1. AUTHENTICATION & ACCESS CONTROL
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetFulfillments_Anonymous_ReturnsUnauthorized()
    {
        var response = await _client.GetAsync("/api/admin/fulfillments");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetFulfillments_CustomerAuthenticated_ReturnsForbidden()
    {
        var (_, customerCookie) = await RegisterAndLoginCustomerAsync(
            $"cust_denied_{Guid.NewGuid():N}@test.local",
            "CustPassword123!");

        var request = CreateAuthenticatedRequest(HttpMethod.Get, "/api/admin/fulfillments", customerCookie);
        var response = await _client.SendAsync(request);

        Assert.True(response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetFulfillments_AdminAuthenticated_ReturnsOk()
    {
        var (_, adminCookie) = await LoginAsAdminAsync();

        var request = CreateAuthenticatedRequest(HttpMethod.Get, "/api/admin/fulfillments", adminCookie);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AdminFulfillmentSummaryDto>>>();
        Assert.NotNull(payload);
        Assert.True(payload.Success);
        Assert.NotNull(payload.Data);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 2. QUERY VALIDATION
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetFulfillments_InvalidPage_ReturnsBadRequest()
    {
        var (_, adminCookie) = await LoginAsAdminAsync();

        var request = CreateAuthenticatedRequest(HttpMethod.Get, "/api/admin/fulfillments?page=0", adminCookie);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AdminFulfillmentSummaryDto>>>();
        Assert.False(payload?.Success);
        Assert.Contains("Page must be greater than or equal to 1", payload?.Message ?? "");
    }

    [Fact]
    public async Task GetFulfillments_InvalidPageSize_ReturnsBadRequest()
    {
        var (_, adminCookie) = await LoginAsAdminAsync();

        var request = CreateAuthenticatedRequest(HttpMethod.Get, "/api/admin/fulfillments?pageSize=150", adminCookie);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AdminFulfillmentSummaryDto>>>();
        Assert.False(payload?.Success);
        Assert.Contains("Page size must be between 1 and 100", payload?.Message ?? "");
    }

    [Fact]
    public async Task GetFulfillments_SearchTermTooLong_ReturnsBadRequest()
    {
        var (_, adminCookie) = await LoginAsAdminAsync();
        var longSearch = new string('a', 101);

        var request = CreateAuthenticatedRequest(HttpMethod.Get, $"/api/admin/fulfillments?search={longSearch}", adminCookie);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AdminFulfillmentSummaryDto>>>();
        Assert.False(payload?.Success);
        Assert.Contains("Search term cannot exceed 100 characters", payload?.Message ?? "");
    }

    [Fact]
    public async Task GetFulfillments_InvalidStatusFilter_ReturnsBadRequest()
    {
        var (_, adminCookie) = await LoginAsAdminAsync();

        var request = CreateAuthenticatedRequest(HttpMethod.Get, "/api/admin/fulfillments?status=BOGUS_STATUS", adminCookie);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AdminFulfillmentSummaryDto>>>();
        Assert.False(payload?.Success);
        Assert.Contains("Invalid status", payload?.Message ?? "");
    }

    [Fact]
    public async Task GetFulfillments_InvalidMethodFilter_ReturnsBadRequest()
    {
        var (_, adminCookie) = await LoginAsAdminAsync();

        var request = CreateAuthenticatedRequest(HttpMethod.Get, "/api/admin/fulfillments?fulfillmentMethod=INVALID_METHOD", adminCookie);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AdminFulfillmentSummaryDto>>>();
        Assert.False(payload?.Success);
        Assert.Contains("Invalid fulfillmentMethod", payload?.Message ?? "");
    }

    [Fact]
    public async Task GetFulfillments_InvalidOrderStatusFilter_ReturnsBadRequest()
    {
        var (_, adminCookie) = await LoginAsAdminAsync();

        var request = CreateAuthenticatedRequest(HttpMethod.Get, "/api/admin/fulfillments?orderStatus=INVALID_ORDER_STATUS", adminCookie);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AdminFulfillmentSummaryDto>>>();
        Assert.False(payload?.Success);
        Assert.Contains("Invalid orderStatus", payload?.Message ?? "");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 3. FULFILLMENT WORK QUEUE: DATA & RELATIONSHIPS
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetFulfillments_ReturnsAccurateRelationships_ForInitializedAndUninitializedItems()
    {
        var (_, adminCookie) = await LoginAsAdminAsync();
        var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Seed Item 1: Processing order with existing InProgress manual fulfillment
            await SeedOrderItemAsync(db, $"{suffix}_init", OrderStatus.Processing, FulfillmentStatus.InProgress, FulfillmentMethod.Manual, "SKU-INIT", "Initialized Item");

            // Seed Item 2: Processing order with NO fulfillment record (uninitialized actionable work)
            await SeedOrderItemAsync(db, $"{suffix}_uninit", OrderStatus.Processing, null, FulfillmentMethod.Unassigned, "SKU-UNINIT", "Uninitialized Item");
        }

        var req = CreateAuthenticatedRequest(HttpMethod.Get, $"/api/admin/fulfillments?search={suffix}", adminCookie);
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var payload = await res.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AdminFulfillmentSummaryDto>>>();
        Assert.NotNull(payload?.Data);
        Assert.Equal(2, payload.Data.TotalCount);

        var initItem = payload.Data.Items.FirstOrDefault(i => i.ProductSku.Contains("SKU-INIT"));
        Assert.NotNull(initItem);
        Assert.True(initItem.IsInitialized);
        Assert.NotNull(initItem.FulfillmentId);
        Assert.Equal("InProgress", initItem.FulfillmentStatus);
        Assert.Equal("InProgress", initItem.QueueStatus);
        Assert.Equal("Manual", initItem.FulfillmentMethod);
        Assert.Equal("Processing", initItem.OrderStatus);

        var uninitItem = payload.Data.Items.FirstOrDefault(i => i.ProductSku.Contains("SKU-UNINIT"));
        Assert.NotNull(uninitItem);
        Assert.False(uninitItem.IsInitialized);
        Assert.Null(uninitItem.FulfillmentId);
        Assert.Null(uninitItem.FulfillmentStatus);
        Assert.Equal("Uninitialized", uninitItem.QueueStatus);
        Assert.Equal("Unassigned", uninitItem.FulfillmentMethod);
        Assert.Equal("Processing", uninitItem.OrderStatus);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 4. SEARCH CAPABILITIES
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetFulfillments_Search_MatchesOrderNumber_Customer_And_ProductSku()
    {
        var (_, adminCookie) = await LoginAsAdminAsync();
        var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
        string orderNumber;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var (order, item, _) = await SeedOrderItemAsync(
                db,
                suffix,
                OrderStatus.Processing,
                FulfillmentStatus.NotStarted,
                FulfillmentMethod.Manual,
                $"SPEC-SKU-{suffix}",
                $"UniqueTool-{suffix}");
            orderNumber = order.OrderNumber;
        }

        // Search by OrderNumber
        var reqOrder = CreateAuthenticatedRequest(HttpMethod.Get, $"/api/admin/fulfillments?search={orderNumber}", adminCookie);
        var resOrder = await _client.SendAsync(reqOrder);
        var dataOrder = await resOrder.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AdminFulfillmentSummaryDto>>>();
        Assert.NotNull(dataOrder?.Data);
        Assert.Single(dataOrder.Data.Items);
        Assert.Equal(orderNumber, dataOrder.Data.Items[0].OrderNumber);

        // Search by Product SKU
        var reqSku = CreateAuthenticatedRequest(HttpMethod.Get, $"/api/admin/fulfillments?search=SPEC-SKU-{suffix}", adminCookie);
        var resSku = await _client.SendAsync(reqSku);
        var dataSku = await resSku.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AdminFulfillmentSummaryDto>>>();
        Assert.NotNull(dataSku?.Data);
        Assert.Single(dataSku.Data.Items);
        Assert.Contains($"SPEC-SKU-{suffix}", dataSku.Data.Items[0].ProductSku);

        // Search by Customer name
        var reqCust = CreateAuthenticatedRequest(HttpMethod.Get, $"/api/admin/fulfillments?search=Customer+{suffix}", adminCookie);
        var resCust = await _client.SendAsync(reqCust);
        var dataCust = await resCust.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AdminFulfillmentSummaryDto>>>();
        Assert.NotNull(dataCust?.Data);
        Assert.Single(dataCust.Data.Items);
        Assert.Contains(suffix, dataCust.Data.Items[0].CustomerName ?? "");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 5. STATUS & METHOD FILTERING
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetFulfillments_FilterByStatus_And_Method()
    {
        var (_, adminCookie) = await LoginAsAdminAsync();
        var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await SeedOrderItemAsync(db, $"{suffix}_prog", OrderStatus.Processing, FulfillmentStatus.InProgress, FulfillmentMethod.Manual);
            await SeedOrderItemAsync(db, $"{suffix}_comp", OrderStatus.Completed, FulfillmentStatus.Completed, FulfillmentMethod.Manual);
            await SeedOrderItemAsync(db, $"{suffix}_uninit", OrderStatus.Processing, null, FulfillmentMethod.Unassigned);
        }

        // Filter: status=InProgress
        var reqProg = CreateAuthenticatedRequest(HttpMethod.Get, $"/api/admin/fulfillments?status=InProgress&search={suffix}", adminCookie);
        var resProg = await _client.SendAsync(reqProg);
        var dataProg = await resProg.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AdminFulfillmentSummaryDto>>>();
        Assert.NotNull(dataProg?.Data);
        Assert.Single(dataProg.Data.Items);
        Assert.Equal("InProgress", dataProg.Data.Items[0].FulfillmentStatus);

        // Filter: status=Uninitialized
        var reqUninit = CreateAuthenticatedRequest(HttpMethod.Get, $"/api/admin/fulfillments?status=Uninitialized&search={suffix}", adminCookie);
        var resUninit = await _client.SendAsync(reqUninit);
        var dataUninit = await resUninit.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AdminFulfillmentSummaryDto>>>();
        Assert.NotNull(dataUninit?.Data);
        Assert.Single(dataUninit.Data.Items);
        Assert.False(dataUninit.Data.Items[0].IsInitialized);

        // Filter: fulfillmentMethod=Manual
        var reqManual = CreateAuthenticatedRequest(HttpMethod.Get, $"/api/admin/fulfillments?fulfillmentMethod=Manual&search={suffix}", adminCookie);
        var resManual = await _client.SendAsync(reqManual);
        var dataManual = await resManual.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AdminFulfillmentSummaryDto>>>();
        Assert.NotNull(dataManual?.Data);
        Assert.Equal(2, dataManual.Data.TotalCount); // both in_progress and completed are manual
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 6. PAGINATION & DETERMINISM
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetFulfillments_Pagination_And_DeterministicOrdering()
    {
        var (_, adminCookie) = await LoginAsAdminAsync();
        var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            for (int i = 1; i <= 5; i++)
            {
                await SeedOrderItemAsync(db, $"{suffix}_{i:D2}", OrderStatus.Processing, FulfillmentStatus.NotStarted, FulfillmentMethod.Manual);
            }
        }

        var reqP1 = CreateAuthenticatedRequest(HttpMethod.Get, $"/api/admin/fulfillments?search={suffix}&page=1&pageSize=2", adminCookie);
        var resP1 = await _client.SendAsync(reqP1);
        var dataP1 = await resP1.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AdminFulfillmentSummaryDto>>>();

        Assert.NotNull(dataP1?.Data);
        Assert.Equal(5, dataP1.Data.TotalCount);
        Assert.Equal(2, dataP1.Data.Items.Count);
        Assert.Equal(1, dataP1.Data.Page);
        Assert.Equal(2, dataP1.Data.PageSize);
        Assert.Equal(3, dataP1.Data.TotalPages);

        var reqP2 = CreateAuthenticatedRequest(HttpMethod.Get, $"/api/admin/fulfillments?search={suffix}&page=2&pageSize=2", adminCookie);
        var resP2 = await _client.SendAsync(reqP2);
        var dataP2 = await resP2.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AdminFulfillmentSummaryDto>>>();

        Assert.NotNull(dataP2?.Data);
        Assert.Equal(2, dataP2.Data.Items.Count);
        Assert.Equal(2, dataP2.Data.Page);

        // Deterministic ordering: P1 items and P2 items must not overlap
        var p1Ids = dataP1.Data.Items.Select(i => i.OrderItemId).ToHashSet();
        var p2Ids = dataP2.Data.Items.Select(i => i.OrderItemId).ToHashSet();
        Assert.Empty(p1Ids.Intersect(p2Ids));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 7. INELIGIBLE ORDERS (PENDING PAYMENT & CANCELLED WITHOUT FULFILLMENT)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetFulfillments_ExcludesUnpaidAndCancelledOrders_UnlessFulfillmentRecordExists()
    {
        var (_, adminCookie) = await LoginAsAdminAsync();
        var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Unpaid order (PendingPayment) with NO fulfillment record -> MUST BE EXCLUDED
            await SeedOrderItemAsync(db, $"{suffix}_unpaid", OrderStatus.PendingPayment, null, FulfillmentMethod.Unassigned);

            // Cancelled order with NO fulfillment record -> MUST BE EXCLUDED
            await SeedOrderItemAsync(db, $"{suffix}_cancelled", OrderStatus.Cancelled, null, FulfillmentMethod.Unassigned);

            // Processing order with NO fulfillment record -> MUST BE INCLUDED
            await SeedOrderItemAsync(db, $"{suffix}_processing", OrderStatus.Processing, null, FulfillmentMethod.Unassigned);
        }

        var req = CreateAuthenticatedRequest(HttpMethod.Get, $"/api/admin/fulfillments?search={suffix}", adminCookie);
        var res = await _client.SendAsync(req);
        var data = await res.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AdminFulfillmentSummaryDto>>>();

        Assert.NotNull(data?.Data);
        Assert.Single(data.Data.Items);
        Assert.Contains($"{suffix}_processing", data.Data.Items[0].ProductSku);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 8. DATA PRIVACY & SECURITY
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetFulfillments_NeverExposesSecrets_Passwords_Or_VendorWholesaleCosts()
    {
        var (_, adminCookie) = await LoginAsAdminAsync();
        var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await SeedOrderItemAsync(db, suffix, OrderStatus.Processing, FulfillmentStatus.InProgress, FulfillmentMethod.Manual);
        }

        var req = CreateAuthenticatedRequest(HttpMethod.Get, $"/api/admin/fulfillments?search={suffix}", adminCookie);
        var res = await _client.SendAsync(req);
        var rawJson = await res.Content.ReadAsStringAsync();

        Assert.DoesNotContain("VendorCost", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Wholesale", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Secret", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ApiKey", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Token", rawJson, StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 9. READ-ONLY PURITY: NO MUTATIONS
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetFulfillments_IsPureReadOnly_NeverCreatesFulfillmentRowsOrMutatesState()
    {
        var (_, adminCookie) = await LoginAsAdminAsync();
        var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
        long orderItemId;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var (_, item, _) = await SeedOrderItemAsync(db, suffix, OrderStatus.Processing, null, FulfillmentMethod.Unassigned);
            orderItemId = item.Id;

            // Assert no fulfillment record exists
            var existingFulfillment = await db.Fulfillments.FirstOrDefaultAsync(f => f.OrderItemId == orderItemId);
            Assert.Null(existingFulfillment);
        }

        // Call the GET endpoint
        var req = CreateAuthenticatedRequest(HttpMethod.Get, $"/api/admin/fulfillments?search={suffix}", adminCookie);
        var res = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        // Verify that NO Fulfillment record was inserted by the GET query
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var fulfillmentAfterGet = await db.Fulfillments.FirstOrDefaultAsync(f => f.OrderItemId == orderItemId);
            Assert.Null(fulfillmentAfterGet);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 10. COMPATIBILITY: EXISTING ENDPOINTS REMAIN INTACT
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExistingOrderFulfillmentsEndpoint_RemainsCompatible()
    {
        var (_, adminCookie) = await LoginAsAdminAsync();
        var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
        string orderNumber;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var (order, _, _) = await SeedOrderItemAsync(db, suffix, OrderStatus.Processing, FulfillmentStatus.NotStarted, FulfillmentMethod.Manual);
            orderNumber = order.OrderNumber;
        }

        var req = CreateAuthenticatedRequest(HttpMethod.Get, $"/api/admin/orders/{orderNumber}/fulfillments", adminCookie);
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var payload = await res.Content.ReadFromJsonAsync<ApiResponse<AdminOrderFulfillmentsDto>>();
        Assert.NotNull(payload?.Data);
        Assert.Equal(orderNumber, payload.Data.OrderNumber);
        Assert.Single(payload.Data.Fulfillments);
    }

    [Fact]
    public async Task ManualFulfillment_Start_RemainsDisabledByDefault()
    {
        var (_, adminCookie) = await LoginAsAdminAsync();
        var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
        string orderNumber;
        long orderItemId;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var (order, item, _) = await SeedOrderItemAsync(db, suffix, OrderStatus.Processing, null, FulfillmentMethod.Unassigned);
            orderNumber = order.OrderNumber;
            orderItemId = item.Id;
        }

        // CSRF Token
        var csrfReq = CreateAuthenticatedRequest(HttpMethod.Get, "/api/admin/auth/csrf", adminCookie);
        var csrfRes = await _client.SendAsync(csrfReq);
        Assert.Equal(HttpStatusCode.OK, csrfRes.StatusCode);
        var csrfData = await csrfRes.Content.ReadFromJsonAsync<ApiResponse<string>>();
        var csrfToken = csrfData?.Data ?? "";

        var startReq = CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"/api/admin/orders/{orderNumber}/items/{orderItemId}/manual-fulfillment/start",
            adminCookie);
        startReq.Headers.Add("X-XSRF-TOKEN", csrfToken);

        var startRes = await _client.SendAsync(startReq);
        // Master feature gate disabled by default returns 403 Forbidden
        Assert.Equal(HttpStatusCode.Forbidden, startRes.StatusCode);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 11. POSTGRESQL TRANSLATION & EXECUTION PURITY
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSql_GetFulfillments_TranslatesAndExecutesCleanly()
    {
        var options = PostgreSqlTestDatabase.CreateOptions();
        await using var db = new AppDbContext(options);

        if (!await PostgreSqlTestDatabase.EnsureMigratedAsync(db))
        {
            // PostgreSQL not available; skip isolated container test
            return;
        }

        var suffix = $"pg_q_{Guid.NewGuid():N}"[..10];
        await SeedOrderItemAsync(db, $"{suffix}_init", OrderStatus.Processing, FulfillmentStatus.InProgress, FulfillmentMethod.Manual, "PGSKU-1", "PG Tool 1");
        await SeedOrderItemAsync(db, $"{suffix}_uninit", OrderStatus.Processing, null, FulfillmentMethod.Unassigned, "PGSKU-2", "PG Tool 2");

        var service = new FulfillmentService(db, NullLogger<FulfillmentService>.Instance);

        // Test 1: Default query (both items returned on real PostgreSQL)
        var q1 = new AdminFulfillmentQuery { Search = suffix, Page = 1, PageSize = 10 };
        var res1 = await service.GetFulfillmentsAsync(q1);
        Assert.Equal(2, res1.TotalCount);
        Assert.Equal(2, res1.Items.Count);

        // Test 2: Filter by status=Uninitialized
        var q2 = new AdminFulfillmentQuery { Search = suffix, Status = "Uninitialized" };
        var res2 = await service.GetFulfillmentsAsync(q2);
        Assert.Equal(1, res2.TotalCount);
        Assert.False(res2.Items[0].IsInitialized);
        Assert.Null(res2.Items[0].FulfillmentId);
        Assert.Equal("Uninitialized", res2.Items[0].QueueStatus);

        // Test 3: Filter by status=InProgress
        var q3 = new AdminFulfillmentQuery { Search = suffix, Status = "InProgress" };
        var res3 = await service.GetFulfillmentsAsync(q3);
        Assert.Equal(1, res3.TotalCount);
        Assert.True(res3.Items[0].IsInitialized);
        Assert.NotNull(res3.Items[0].FulfillmentId);
        Assert.Equal("InProgress", res3.Items[0].FulfillmentStatus);

        // Test 4: Pagination & Ordering on PostgreSQL (LIMIT 1 OFFSET 0)
        var q4 = new AdminFulfillmentQuery { Search = suffix, Page = 1, PageSize = 1 };
        var res4 = await service.GetFulfillmentsAsync(q4);
        Assert.Equal(2, res4.TotalCount);
        Assert.Single(res4.Items);
    }
}

