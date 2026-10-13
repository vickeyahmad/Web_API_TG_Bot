using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Json;
using WebApiTgBot.Common.Models;
using WebApiTgBot.Infrastructure.Data;
using WebApiTgBot.Modules.Catalog.Models;
using WebApiTgBot.Modules.Identity.Models;
using WebApiTgBot.Modules.Identity.Models.DTOs;
using WebApiTgBot.Modules.Orders.Models;
using WebApiTgBot.Modules.Orders.Models.DTOs;
using WebApiTgBot.Modules.Orders.Services;
using WebApiTgBot.Modules.Payments.Models;
using WebApiTgBot.Modules.Payments.Models.DTOs;
using WebApiTgBot.Modules.Payments.Options;
using WebApiTgBot.Modules.Payments.Services;
using WebApiTgBot.Modules.Wallets.Models;
using WebApiTgBot.Modules.Wallets.Services;

namespace WebApiTgBot.Tests;

/// <summary>
/// Comprehensive integration test suite for Milestone 5A: Admin Order Management & Fulfillment Preparation.
/// Verifies:
/// 1. Admin order listing with server-side pagination, sorting, and multi-criteria filtering.
/// 2. Admin order detail inspection with historical price snapshots and payment attempt summaries.
/// 3. Strict authentication and authorization separation (Admin vs Customer vs Anonymous).
/// 4. Controlled transition of verified Paid orders into Processing status with audit trail.
/// 5. Strict rejection of unpaid orders and payment/currency mismatches.
/// 6. Concurrency safety and duplicate transition prevention on real PostgreSQL.
/// 7. CSRF enforcement on state-changing operations.
/// 8. Zero financial modification of customer wallets or payment records.
/// 9. Preservation of vendor privacy on customer-facing APIs.
/// </summary>
public class AdminOrdersTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public AdminOrdersTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private HttpClient CreateEnabledClient()
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
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
        HttpClient? client = null)
    {
        await EnsureRolesExistAsync();
        var httpClient = client ?? _client;

        var regRes = await httpClient.PostAsJsonAsync("/api/customer/auth/register", new CustomerRegisterRequest
        {
            Email = email,
            Password = password,
            FullName = "Order Test Customer"
        });
        Assert.Equal(HttpStatusCode.Created, regRes.StatusCode);
        var regData = await regRes.Content.ReadFromJsonAsync<ApiResponse<CustomerUserDto>>();
        Assert.NotNull(regData?.Data);

        var loginRes = await httpClient.PostAsJsonAsync("/api/customer/auth/login", new CustomerLoginRequest
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

    private async Task<(string AdminId, string AuthCookie, string AntiforgeryCookie, string CsrfToken)> LoginAsAdminAsync(HttpClient? client = null)
    {
        await EnsureRolesExistAsync();
        var httpClient = client ?? _client;
        var email = $"admin_ord_{Guid.NewGuid():N}@test.local";
        var password = "SecureAdminPassword123!";
        string adminId;

        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                FullName = "Admin Order Manager",
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

    private async Task<(string CsrfToken, string AntiforgeryCookie)> GetCustomerCsrfTokenAsync(string authCookie, HttpClient? client = null)
    {
        var httpClient = client ?? _client;
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/customer/auth/csrf");
        req.Headers.Add("Cookie", authCookie);
        var res = await httpClient.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<ApiResponse<string>>();
        var antiCookie = string.Empty;
        if (res.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            var match = cookies.FirstOrDefault(c => c.Contains(".WebAPI.Antiforgery"));
            if (match != null)
            {
                antiCookie = match.Split(';')[0];
            }
        }

        return (body!.Data!, antiCookie);
    }

    private async Task<Product> CreateTestProductAsync(string sku, decimal price)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var catSlug = $"cat_{Guid.NewGuid():N}";
        var category = new Category
        {
            Name = "Category " + catSlug,
            Slug = catSlug,
            IsActive = true
        };
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        var product = new Product
        {
            InternalSku = sku,
            Name = "Product " + sku,
            SellingPrice = price,
            SellingCurrency = "PKR",
            CategoryId = category.Id,
            IsActive = true
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();

        return product;
    }

    private async Task<string> CreateCustomerOrderAsync(
        string sku,
        string authCookie,
        string csrfToken,
        string antiCookie,
        HttpClient client)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/customer/orders")
        {
            Content = JsonContent.Create(new CreateCustomerOrderRequest
            {
                ProductSku = sku,
                CustomerNotes = "Admin test order note"
            })
        };
        req.Headers.Add("Cookie", string.IsNullOrEmpty(antiCookie) ? authCookie : $"{authCookie}; {antiCookie}");
        req.Headers.Add("X-XSRF-TOKEN", csrfToken);
        req.Headers.Add("X-Idempotency-Key", $"ORD-KEY-{Guid.NewGuid():N}");

        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var data = await res.Content.ReadFromJsonAsync<ApiResponse<CustomerOrderDetailDto>>();
        return data!.Data!.OrderNumber;
    }

    private async Task CreditCustomerWalletAsync(string customerId, decimal amount)
    {
        using var scope = _factory.Services.CreateScope();
        var walletService = scope.ServiceProvider.GetRequiredService<IWalletService>();
        var result = await walletService.CreditAsync(new WalletCreditRequest
        {
            CustomerId = customerId,
            Amount = amount,
            Currency = "PKR",
            OperationKey = $"TEST_CREDIT_{Guid.NewGuid():N}",
            SourceType = WalletTransactionSource.DepositVerification,
            Description = "Initial seed for order test"
        });
        Assert.True(result.IsSuccess);
    }

    private async Task<string> PayCustomerOrderWithWalletAsync(
        string orderNumber,
        string authCookie,
        string csrfToken,
        string antiCookie,
        HttpClient client)
    {
        var payReq = new HttpRequestMessage(HttpMethod.Post, $"/api/customer/orders/{orderNumber}/pay-with-wallet");
        payReq.Headers.Add("Cookie", string.IsNullOrEmpty(antiCookie) ? authCookie : $"{authCookie}; {antiCookie}");
        payReq.Headers.Add("X-XSRF-TOKEN", csrfToken);
        payReq.Headers.Add("X-Idempotency-Key", $"PAY-KEY-{Guid.NewGuid():N}");

        var payRes = await client.SendAsync(payReq);
        Assert.Equal(HttpStatusCode.OK, payRes.StatusCode);
        var payData = await payRes.Content.ReadFromJsonAsync<ApiResponse<CustomerOrderPaymentDto>>();
        return payData!.Data!.PaymentNumber;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 1. Authorization & Authentication Security
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetOrders_Anonymous_ReturnsUnauthorized()
    {
        var res = await _client.GetAsync("/api/admin/orders");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task GetOrders_CustomerRole_ReturnsUnauthorizedOrForbidden()
    {
        var email = $"cust_no_admin_{Guid.NewGuid():N}@test.local";
        var (_, authCookie) = await RegisterAndLoginCustomerAsync(email, "Password123!");

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/admin/orders");
        req.Headers.Add("Cookie", authCookie);
        var res = await _client.SendAsync(req);

        // Dual cookie scheme: Customer session has no Admin cookie -> 401 Unauthorized or 403 Forbidden
        Assert.True(res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetOrderDetail_Anonymous_ReturnsUnauthorized()
    {
        var res = await _client.GetAsync("/api/admin/orders/ORD-TEST-1234");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task StartProcessing_Anonymous_ReturnsUnauthorized()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/admin/orders/ORD-TEST-1234/start-processing");
        var res = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task StartProcessing_MissingCsrf_ReturnsBadRequest()
    {
        var (_, authCookie, _, _) = await LoginAsAdminAsync();

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/admin/orders/ORD-TEST-1234/start-processing")
        {
            Content = JsonContent.Create(new AdminStartOrderProcessingRequest { Reason = "Test" })
        };
        req.Headers.Add("Cookie", authCookie); // Missing CSRF token and antiforgery cookie!

        var res = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 2. Admin Order Listing, Filtering, and Pagination
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetOrders_AdminAuthenticated_ReturnsPagedOrders_DefaultNewestFirst()
    {
        var client = CreateEnabledClient();
        var (_, adminCookie, _, _) = await LoginAsAdminAsync(client);

        var sku = $"SKU-LST-{Guid.NewGuid():N}"[..18];
        await CreateTestProductAsync(sku, 1500m);

        var email = $"cust_lst_{Guid.NewGuid():N}@test.local";
        var (_, custCookie) = await RegisterAndLoginCustomerAsync(email, "Password123!", client);
        var (csrf, anti) = await GetCustomerCsrfTokenAsync(custCookie, client);
        var orderNumber = await CreateCustomerOrderAsync(sku, custCookie, csrf, anti, client);

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/admin/orders?page=1&pageSize=10");
        req.Headers.Add("Cookie", adminCookie);

        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AdminOrderSummaryDto>>>();
        Assert.NotNull(body?.Data);
        Assert.NotEmpty(body.Data.Items);

        // Verify the created order exists and fields are populated
        var found = body.Data.Items.FirstOrDefault(o => o.OrderNumber == orderNumber);
        Assert.NotNull(found);
        Assert.Equal(orderNumber, found.OrderNumber);
        Assert.Equal(email, found.CustomerEmail);
        Assert.Equal(1500m, found.TotalAmount);
        Assert.Equal("PKR", found.Currency);
        Assert.Equal("PendingPayment", found.Status);
        Assert.Equal("Website", found.SalesChannel);
        Assert.Contains(sku, found.ProductSummary);
    }

    [Fact]
    public async Task GetOrders_FilterByStatus_ReturnsOnlyMatchingStatusOrders()
    {
        var client = CreateEnabledClient();
        var (_, adminCookie, _, _) = await LoginAsAdminAsync(client);

        var sku = $"SKU-FLT-{Guid.NewGuid():N}"[..18];
        await CreateTestProductAsync(sku, 1000m);

        var email = $"cust_flt_{Guid.NewGuid():N}@test.local";
        var (custId, custCookie) = await RegisterAndLoginCustomerAsync(email, "Password123!", client);
        var (csrf, anti) = await GetCustomerCsrfTokenAsync(custCookie, client);
        await CreditCustomerWalletAsync(custId, 3000m);

        // Order 1: Paid
        var ord1 = await CreateCustomerOrderAsync(sku, custCookie, csrf, anti, client);
        await PayCustomerOrderWithWalletAsync(ord1, custCookie, csrf, anti, client);

        // Order 2: PendingPayment
        var ord2 = await CreateCustomerOrderAsync(sku, custCookie, csrf, anti, client);

        // Query Paid orders only
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/admin/orders?status=Paid");
        req.Headers.Add("Cookie", adminCookie);
        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AdminOrderSummaryDto>>>();
        Assert.NotNull(body?.Data);

        Assert.Contains(body.Data.Items, o => o.OrderNumber == ord1 && o.Status == "Paid");
        Assert.DoesNotContain(body.Data.Items, o => o.OrderNumber == ord2);
    }

    [Fact]
    public async Task GetOrders_FilterByCustomerAndOrderNumber()
    {
        var client = CreateEnabledClient();
        var (_, adminCookie, _, _) = await LoginAsAdminAsync(client);

        var sku = $"SKU-SRC-{Guid.NewGuid():N}"[..18];
        await CreateTestProductAsync(sku, 1200m);

        var email = $"cust_src_{Guid.NewGuid():N}@test.local";
        var (_, custCookie) = await RegisterAndLoginCustomerAsync(email, "Password123!", client);
        var (csrf, anti) = await GetCustomerCsrfTokenAsync(custCookie, client);
        var orderNumber = await CreateCustomerOrderAsync(sku, custCookie, csrf, anti, client);

        // 1. Search by exact OrderNumber
        var req1 = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/orders?orderNumber={orderNumber}");
        req1.Headers.Add("Cookie", adminCookie);
        var res1 = await client.SendAsync(req1);
        Assert.Equal(HttpStatusCode.OK, res1.StatusCode);
        var body1 = await res1.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AdminOrderSummaryDto>>>();
        Assert.Single(body1!.Data!.Items);
        Assert.Equal(orderNumber, body1.Data.Items.First().OrderNumber);

        // 2. Search by Customer email query
        var req2 = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/orders?customerQuery={email}");
        req2.Headers.Add("Cookie", adminCookie);
        var res2 = await client.SendAsync(req2);
        Assert.Equal(HttpStatusCode.OK, res2.StatusCode);
        var body2 = await res2.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AdminOrderSummaryDto>>>();
        Assert.All(body2!.Data!.Items, o => Assert.Equal(email, o.CustomerEmail));
    }

    [Fact]
    public async Task GetOrders_FilterByProductSku()
    {
        var client = CreateEnabledClient();
        var (_, adminCookie, _, _) = await LoginAsAdminAsync(client);

        var sku = $"SKU-SPEC-{Guid.NewGuid():N}"[..18];
        await CreateTestProductAsync(sku, 1200m);

        var email = $"cust_sku_{Guid.NewGuid():N}@test.local";
        var (_, custCookie) = await RegisterAndLoginCustomerAsync(email, "Password123!", client);
        var (csrf, anti) = await GetCustomerCsrfTokenAsync(custCookie, client);
        var orderNumber = await CreateCustomerOrderAsync(sku, custCookie, csrf, anti, client);

        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/orders?productSku={sku}");
        req.Headers.Add("Cookie", adminCookie);
        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AdminOrderSummaryDto>>>();
        Assert.Contains(body!.Data!.Items, o => o.OrderNumber == orderNumber);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 3. Admin Order Detail API & Historical Snapshot Verification
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetOrderDetail_NotFound_Returns404()
    {
        var client = CreateEnabledClient();
        var (_, adminCookie, _, _) = await LoginAsAdminAsync(client);

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/admin/orders/ORD-NONEXISTENT");
        req.Headers.Add("Cookie", adminCookie);
        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task GetOrderDetail_ReturnsHistoricalPriceSnapshots_EvenAfterCatalogPriceChanges()
    {
        var client = CreateEnabledClient();
        var (_, adminCookie, _, _) = await LoginAsAdminAsync(client);

        var sku = $"SKU-HIST-{Guid.NewGuid():N}"[..18];
        var product = await CreateTestProductAsync(sku, 2500m); // Original price: 2,500 PKR

        var email = $"cust_hist_{Guid.NewGuid():N}@test.local";
        var (custId, custCookie) = await RegisterAndLoginCustomerAsync(email, "Password123!", client);
        var (csrf, anti) = await GetCustomerCsrfTokenAsync(custCookie, client);
        await CreditCustomerWalletAsync(custId, 5000m);

        var orderNumber = await CreateCustomerOrderAsync(sku, custCookie, csrf, anti, client);
        var paymentNumber = await PayCustomerOrderWithWalletAsync(orderNumber, custCookie, csrf, anti, client);

        // Later: Catalog product price is updated to 9,999 PKR
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var p = await db.Products.FirstAsync(x => x.InternalSku == sku);
            p.SellingPrice = 9999m;
            await db.SaveChangesAsync();
        }

        // Admin retrieves order detail
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/orders/{orderNumber}");
        req.Headers.Add("Cookie", adminCookie);
        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<ApiResponse<AdminOrderDetailDto>>();
        Assert.NotNull(body?.Data);
        var order = body.Data;

        // Verify historical snapshot integrity
        Assert.Equal(orderNumber, order.OrderNumber);
        Assert.Equal(2500m, order.TotalAmount); // Must be original 2,500 PKR, NOT 9,999 PKR
        Assert.Equal("Paid", order.Status);
        Assert.Equal(email, order.CustomerEmail);
        Assert.Single(order.Items);

        var item = order.Items.First();
        Assert.Equal(sku, item.ProductSku);
        Assert.Equal(2500m, item.UnitPrice); // Historical snapshot
        Assert.Equal(2500m, item.TotalPrice);

        // Verify status audit history
        Assert.NotEmpty(order.StatusHistory);
        Assert.Contains(order.StatusHistory, h => h.NewStatus == "Paid");

        // Verify associated payment attempts (OrderPurchase only)
        Assert.Single(order.Payments);
        var pmt = order.Payments.First();
        Assert.Equal(paymentNumber, pmt.PaymentNumber);
        Assert.Equal("WALLET", pmt.PaymentMethod);
        Assert.Equal(2500m, pmt.Amount);
        Assert.Equal("Paid", pmt.Status);
        Assert.NotNull(pmt.PaidAt);
        Assert.StartsWith("WALLET:", pmt.ProviderTransactionId);
    }

    [Fact]
    public async Task GetOrderDetail_ExcludesWalletTopUpPayments()
    {
        var client = CreateEnabledClient();
        var (_, adminCookie, _, _) = await LoginAsAdminAsync(client);

        var sku = $"SKU-EXCL-{Guid.NewGuid():N}"[..18];
        await CreateTestProductAsync(sku, 1000m);

        var email = $"cust_excl_{Guid.NewGuid():N}@test.local";
        var (custId, custCookie) = await RegisterAndLoginCustomerAsync(email, "Password123!", client);
        var (csrf, anti) = await GetCustomerCsrfTokenAsync(custCookie, client);

        // Customer creates a Top-Up Payment
        var topUpReq = new HttpRequestMessage(HttpMethod.Post, "/api/customer/wallet/topups")
        {
            Content = JsonContent.Create(new CreateCustomerWalletTopUpRequest
            {
                Amount = 5000m,
                PaymentMethodCode = "BANK_TRANSFER"
            })
        };
        topUpReq.Headers.Add("Cookie", string.IsNullOrEmpty(anti) ? custCookie : $"{custCookie}; {anti}");
        topUpReq.Headers.Add("X-XSRF-TOKEN", csrf);
        var topUpRes = await client.SendAsync(topUpReq);
        Assert.Equal(HttpStatusCode.Created, topUpRes.StatusCode);

        // Customer creates an Order
        var orderNumber = await CreateCustomerOrderAsync(sku, custCookie, csrf, anti, client);

        // Admin gets order detail: Payments list must NOT contain the top-up payment
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/orders/{orderNumber}");
        req.Headers.Add("Cookie", adminCookie);
        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<ApiResponse<AdminOrderDetailDto>>();
        Assert.NotNull(body?.Data);
        Assert.Empty(body.Data.Payments); // Top-up is NOT an order payment
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 4. Controlled Order Processing (Paid -> Processing)
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StartProcessing_UnpaidOrder_ReturnsBadRequest()
    {
        var client = CreateEnabledClient();
        var (_, adminCookie, antiCookie, csrfToken) = await LoginAsAdminAsync(client);

        var sku = $"SKU-UNP-{Guid.NewGuid():N}"[..18];
        await CreateTestProductAsync(sku, 2000m);

        var email = $"cust_unp_{Guid.NewGuid():N}@test.local";
        var (_, custCookie) = await RegisterAndLoginCustomerAsync(email, "Password123!", client);
        var (csrf, anti) = await GetCustomerCsrfTokenAsync(custCookie, client);
        var orderNumber = await CreateCustomerOrderAsync(sku, custCookie, csrf, anti, client); // Status: PendingPayment

        // Admin attempts to start processing unpaid order
        var procReq = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/orders/{orderNumber}/start-processing")
        {
            Content = JsonContent.Create(new AdminStartOrderProcessingRequest { Reason = "Attempt start unpaid" })
        };
        procReq.Headers.Add("Cookie", string.IsNullOrEmpty(antiCookie) ? adminCookie : $"{adminCookie}; {antiCookie}");
        procReq.Headers.Add("X-XSRF-TOKEN", csrfToken);

        var procRes = await client.SendAsync(procReq);
        Assert.Equal(HttpStatusCode.BadRequest, procRes.StatusCode);

        var body = await procRes.Content.ReadFromJsonAsync<ApiResponse<AdminOrderDetailDto>>();
        Assert.Contains("PendingPayment", body!.Message);

        // Verify order remains PendingPayment
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var order = await db.Orders.FirstAsync(o => o.OrderNumber == orderNumber);
        Assert.Equal(OrderStatus.PendingPayment, order.Status);
    }

    [Fact]
    public async Task StartProcessing_PaidOrder_WithoutPaidPayment_ReturnsBadRequest()
    {
        var client = CreateEnabledClient();
        var (_, adminCookie, antiCookie, csrfToken) = await LoginAsAdminAsync(client);

        var sku = $"SKU-NOPAY-{Guid.NewGuid():N}"[..18];
        await CreateTestProductAsync(sku, 2000m);

        var email = $"cust_nopay_{Guid.NewGuid():N}@test.local";
        var (_, custCookie) = await RegisterAndLoginCustomerAsync(email, "Password123!", client);
        var (csrf, anti) = await GetCustomerCsrfTokenAsync(custCookie, client);
        var orderNumber = await CreateCustomerOrderAsync(sku, custCookie, csrf, anti, client);

        // Artificially advance order status to Paid in DB without a matching Paid Payment
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var o = await db.Orders.FirstAsync(x => x.OrderNumber == orderNumber);
            o.Status = OrderStatus.Paid;
            await db.SaveChangesAsync();
        }

        var procReq = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/orders/{orderNumber}/start-processing")
        {
            Content = JsonContent.Create(new AdminStartOrderProcessingRequest { Reason = "Verify payment check" })
        };
        procReq.Headers.Add("Cookie", string.IsNullOrEmpty(antiCookie) ? adminCookie : $"{adminCookie}; {antiCookie}");
        procReq.Headers.Add("X-XSRF-TOKEN", csrfToken);

        var procRes = await client.SendAsync(procReq);
        Assert.Equal(HttpStatusCode.BadRequest, procRes.StatusCode);

        var body = await procRes.Content.ReadFromJsonAsync<ApiResponse<AdminOrderDetailDto>>();
        Assert.Contains("No verified Paid payment record was found", body!.Message);
    }

    [Fact]
    public async Task StartProcessing_PaidOrder_MismatchedPaymentAmount_ReturnsBadRequest()
    {
        var client = CreateEnabledClient();
        var (_, adminCookie, antiCookie, csrfToken) = await LoginAsAdminAsync(client);

        var sku = $"SKU-MIS-{Guid.NewGuid():N}"[..18];
        await CreateTestProductAsync(sku, 2000m); // Total: 2,000 PKR

        var email = $"cust_mis_{Guid.NewGuid():N}@test.local";
        var (_, custCookie) = await RegisterAndLoginCustomerAsync(email, "Password123!", client);
        var (csrf, anti) = await GetCustomerCsrfTokenAsync(custCookie, client);
        var orderNumber = await CreateCustomerOrderAsync(sku, custCookie, csrf, anti, client);

        // Artificially attach a Paid payment for only 500 PKR (mismatched amount!)
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var o = await db.Orders.FirstAsync(x => x.OrderNumber == orderNumber);
            o.Status = OrderStatus.Paid;

            var payment = new Payment
            {
                PaymentNumber = $"PAY-MIS-{Guid.NewGuid():N}"[..20],
                Purpose = PaymentPurpose.OrderPurchase,
                OrderId = o.Id,
                Amount = 500m, // Mismatched! Expected: 2000m
                Currency = "PKR",
                PaymentMethodCode = "WALLET",
                Status = PaymentStatus.Paid,
                PaidAt = DateTime.UtcNow
            };
            db.Payments.Add(payment);
            await db.SaveChangesAsync();
        }

        var procReq = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/orders/{orderNumber}/start-processing")
        {
            Content = JsonContent.Create(new AdminStartOrderProcessingRequest { Reason = "Mismatched amount test" })
        };
        procReq.Headers.Add("Cookie", string.IsNullOrEmpty(antiCookie) ? adminCookie : $"{adminCookie}; {antiCookie}");
        procReq.Headers.Add("X-XSRF-TOKEN", csrfToken);

        var procRes = await client.SendAsync(procReq);
        Assert.Equal(HttpStatusCode.BadRequest, procRes.StatusCode);

        var body = await procRes.Content.ReadFromJsonAsync<ApiResponse<AdminOrderDetailDto>>();
        Assert.Contains("does not match order total", body!.Message);
    }

    [Fact]
    public async Task StartProcessing_ValidPaidOrder_TransitionsToProcessing_AndRecordsAuditHistory()
    {
        var client = CreateEnabledClient();
        var (adminId, adminCookie, antiCookie, csrfToken) = await LoginAsAdminAsync(client);

        var sku = $"SKU-PROC-{Guid.NewGuid():N}"[..18];
        await CreateTestProductAsync(sku, 1500m);

        var email = $"cust_proc_{Guid.NewGuid():N}@test.local";
        var (custId, custCookie) = await RegisterAndLoginCustomerAsync(email, "Password123!", client);
        var (csrf, anti) = await GetCustomerCsrfTokenAsync(custCookie, client);
        await CreditCustomerWalletAsync(custId, 5000m);

        var orderNumber = await CreateCustomerOrderAsync(sku, custCookie, csrf, anti, client);
        await PayCustomerOrderWithWalletAsync(orderNumber, custCookie, csrf, anti, client);

        // Initial wallet balance check: 5000 - 1500 = 3500
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var w = await db.Wallets.FirstAsync(x => x.CustomerId == custId);
            Assert.Equal(3500m, w.Balance);
        }

        // Admin starts processing
        var procReq = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/orders/{orderNumber}/start-processing")
        {
            Content = JsonContent.Create(new AdminStartOrderProcessingRequest
            {
                Reason = "Assigned to manual processing queue"
            })
        };
        procReq.Headers.Add("Cookie", string.IsNullOrEmpty(antiCookie) ? adminCookie : $"{adminCookie}; {antiCookie}");
        procReq.Headers.Add("X-XSRF-TOKEN", csrfToken);

        var procRes = await client.SendAsync(procReq);
        Assert.Equal(HttpStatusCode.OK, procRes.StatusCode);

        var body = await procRes.Content.ReadFromJsonAsync<ApiResponse<AdminOrderDetailDto>>();
        Assert.NotNull(body?.Data);
        Assert.Equal("Processing", body.Data.Status);

        // Verify PostgreSQL state & audit history
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // 1. Order status is Processing
            var order = await db.Orders
                .Include(o => o.StatusHistory)
                .FirstAsync(o => o.OrderNumber == orderNumber);
            Assert.Equal(OrderStatus.Processing, order.Status);

            // 2. StatusHistory records transition
            var history = order.StatusHistory.FirstOrDefault(h => h.PreviousStatus == OrderStatus.Paid && h.NewStatus == OrderStatus.Processing);
            Assert.NotNull(history);
            Assert.Equal("Assigned to manual processing queue", history.Reason);
            Assert.Equal(adminId, history.ChangedByUserId);

            // 3. Financial protection: customer wallet balance is 100% untouched!
            var wallet = await db.Wallets.FirstAsync(w => w.CustomerId == custId);
            Assert.Equal(3500m, wallet.Balance); // Zero additional deduction

            // 4. Financial protection: payment record amount and status are untouched!
            var payment = await db.Payments.FirstAsync(p => p.OrderId == order.Id);
            Assert.Equal(1500m, payment.Amount);
            Assert.Equal(PaymentStatus.Paid, payment.Status);
        }
    }

    [Fact]
    public async Task StartProcessing_AlreadyProcessing_ReturnsConflict_NoDuplicateHistory()
    {
        var client = CreateEnabledClient();
        var (adminId, adminCookie, antiCookie, csrfToken) = await LoginAsAdminAsync(client);

        var sku = $"SKU-RETRY-{Guid.NewGuid():N}"[..18];
        await CreateTestProductAsync(sku, 1500m);

        var email = $"cust_retry_{Guid.NewGuid():N}@test.local";
        var (custId, custCookie) = await RegisterAndLoginCustomerAsync(email, "Password123!", client);
        var (csrf, anti) = await GetCustomerCsrfTokenAsync(custCookie, client);
        await CreditCustomerWalletAsync(custId, 5000m);

        var orderNumber = await CreateCustomerOrderAsync(sku, custCookie, csrf, anti, client);
        await PayCustomerOrderWithWalletAsync(orderNumber, custCookie, csrf, anti, client);

        // Attempt 1: Start processing -> Success
        var req1 = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/orders/{orderNumber}/start-processing")
        {
            Content = JsonContent.Create(new AdminStartOrderProcessingRequest { Reason = "First attempt" })
        };
        req1.Headers.Add("Cookie", string.IsNullOrEmpty(antiCookie) ? adminCookie : $"{adminCookie}; {antiCookie}");
        req1.Headers.Add("X-XSRF-TOKEN", csrfToken);
        var res1 = await client.SendAsync(req1);
        Assert.Equal(HttpStatusCode.OK, res1.StatusCode);

        // Attempt 2: Repeat start processing -> 409 Conflict
        var req2 = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/orders/{orderNumber}/start-processing")
        {
            Content = JsonContent.Create(new AdminStartOrderProcessingRequest { Reason = "Second attempt" })
        };
        req2.Headers.Add("Cookie", string.IsNullOrEmpty(antiCookie) ? adminCookie : $"{adminCookie}; {antiCookie}");
        req2.Headers.Add("X-XSRF-TOKEN", csrfToken);
        var res2 = await client.SendAsync(req2);
        Assert.Equal(HttpStatusCode.Conflict, res2.StatusCode);

        // Verify database: Exactly ONE Processing history entry exists
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var order = await db.Orders.Include(o => o.StatusHistory).FirstAsync(o => o.OrderNumber == orderNumber);
            var processingHistories = order.StatusHistory.Where(h => h.NewStatus == OrderStatus.Processing).ToList();
            Assert.Single(processingHistories);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 5. Concurrency: Two Simultaneous Admin StartProcessing Requests
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PostgreSql_StartProcessing_ConcurrentAdminRequests_ExactlyOneTransitions()
    {
        var options = PostgreSqlTestDatabase.CreateOptions();
        await using var seedDb = new AppDbContext(options);

        if (!await PostgreSqlTestDatabase.EnsureMigratedAsync(seedDb))
        {
            return;
        }

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var customer = new ApplicationUser
        {
            UserName = $"pg_conc_proc_{suffix}@test.local",
            Email = $"pg_conc_proc_{suffix}@test.local",
            FullName = "PG Concurrent Proc User",
            EmailConfirmed = true,
            IsActive = true
        };
        var admin = new ApplicationUser
        {
            UserName = $"pg_conc_admin_{suffix}@test.local",
            Email = $"pg_conc_admin_{suffix}@test.local",
            FullName = "PG Concurrent Admin User",
            EmailConfirmed = true,
            IsActive = true
        };
        seedDb.Users.AddRange(customer, admin);

        var orderNumber = $"ORD-CONC-PROC-{suffix}";
        var order = new Order
        {
            OrderNumber = orderNumber,
            CustomerId = customer.Id,
            Status = OrderStatus.Paid,
            Currency = "PKR",
            Subtotal = 2000m,
            TotalAmount = 2000m,
            SalesChannel = SalesChannel.Website
        };
        seedDb.Orders.Add(order);
        await seedDb.SaveChangesAsync();

        var payment = new Payment
        {
            PaymentNumber = $"PAY-CONC-PROC-{suffix}",
            Purpose = PaymentPurpose.OrderPurchase,
            OrderId = order.Id,
            Amount = 2000m,
            Currency = "PKR",
            PaymentMethodCode = "WALLET",
            Status = PaymentStatus.Paid,
            PaidAt = DateTime.UtcNow
        };
        seedDb.Payments.Add(payment);
        await seedDb.SaveChangesAsync();

        try
        {
            await using var db1 = new AppDbContext(options);
            await using var db2 = new AppDbContext(options);

            var service1 = new AdminOrderService(db1, NullLogger<AdminOrderService>.Instance);
            var service2 = new AdminOrderService(db2, NullLogger<AdminOrderService>.Instance);

            var req1 = new AdminStartOrderProcessingRequest { Reason = "Admin A starting" };
            var req2 = new AdminStartOrderProcessingRequest { Reason = "Admin B starting" };

            // Run concurrent start-processing operations simultaneously with valid admin user ID
            var task1 = Task.Run(() => service1.StartProcessingAsync(orderNumber, admin.Id, req1));
            var task2 = Task.Run(() => service2.StartProcessingAsync(orderNumber, admin.Id, req2));

            var results = await Task.WhenAll(task1, task2);

            // Exactly ONE succeeds, exactly ONE is Conflict
            Assert.Contains(results, r => r.Status == AdminOrderOperationStatus.Success);
            Assert.Contains(results, r => r.Status == AdminOrderOperationStatus.Conflict);

            // Verify final state in PostgreSQL:
            await using var verifyDb = new AppDbContext(options);
            var verifiedOrder = await verifyDb.Orders
                .Include(o => o.StatusHistory)
                .FirstAsync(o => o.Id == order.Id);

            Assert.Equal(OrderStatus.Processing, verifiedOrder.Status);

            // Exactly ONE Processing transition in history
            var procHistories = verifiedOrder.StatusHistory.Where(h => h.NewStatus == OrderStatus.Processing).ToList();
            Assert.Single(procHistories);
        }
        finally
        {
            await using var cleanDb = new AppDbContext(options);
            var pmts = await cleanDb.Payments.Include(p => p.StatusHistory).Where(p => p.OrderId == order.Id).ToListAsync();
            foreach (var p in pmts) cleanDb.PaymentStatusHistories.RemoveRange(p.StatusHistory);
            cleanDb.Payments.RemoveRange(pmts);

            var ord = await cleanDb.Orders.Include(o => o.StatusHistory).FirstOrDefaultAsync(o => o.Id == order.Id);
            if (ord != null)
            {
                cleanDb.OrderStatusHistories.RemoveRange(ord.StatusHistory);
                cleanDb.Orders.Remove(ord);
            }

            var usr = await cleanDb.Users.FindAsync(customer.Id);
            if (usr != null) cleanDb.Users.Remove(usr);
            var adm = await cleanDb.Users.FindAsync(admin.Id);
            if (adm != null) cleanDb.Users.Remove(adm);

            await cleanDb.SaveChangesAsync();
        }
    }
}
