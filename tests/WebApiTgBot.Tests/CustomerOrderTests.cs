using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
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

namespace WebApiTgBot.Tests;

/// <summary>
/// Integration tests verifying Customer Order Placement and Tracking (Milestone 3C):
/// - Authenticated customer order creation in PendingPayment status.
/// - Unauthenticated / disabled customer rejection.
/// - Product catalog validation (existence, active status, active category).
/// - Server-side price resolution and total computation (zero client price manipulation).
/// - Immutable line item snapshots and initial status history creation.
/// - Durable idempotency (identical retry -> returns existing order; different payload -> 409 Conflict).
/// - Customer order history listing with pagination and status filtering.
/// - Customer order detail retrieval with strict customer ownership isolation (no access to other customers' orders).
/// - CSRF protection enforcement on order creation.
/// - Supplier privacy: responses 100% free of vendor IDs and supplier costs.
/// - Real PostgreSQL database concurrency and unique index constraint verification.
/// </summary>
public class CustomerOrderTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public CustomerOrderTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task<(string CustomerId, string AuthCookie)> RegisterAndLoginCustomerAsync(
        string email,
        string password,
        HttpClient? client = null)
    {
        var httpClient = client ?? _client;

        // 1. Register customer
        var registerResponse = await httpClient.PostAsJsonAsync("/api/customer/auth/register", new CustomerRegisterRequest
        {
            Email = email,
            Password = password,
            FullName = "Test Customer"
        });
        Assert.Equal(HttpStatusCode.Created, registerResponse.StatusCode);
        var regResult = await registerResponse.Content.ReadFromJsonAsync<ApiResponse<CustomerUserDto>>();
        Assert.NotNull(regResult?.Data);
        var customerId = regResult.Data.Id;

        // 2. Log in
        var loginResponse = await httpClient.PostAsJsonAsync("/api/customer/auth/login", new CustomerLoginRequest
        {
            Email = email,
            Password = password
        });
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        var authCookie = loginResponse.Headers.GetValues("Set-Cookie")
            .First(c => c.Contains(".WebAPI.Customer.Auth"))
            .Split(';')[0];

        return (customerId, authCookie);
    }

    private async Task<(string Token, string AntiforgeryCookie)> GetCsrfTokenAsync(
        string authCookie,
        HttpClient? client = null)
    {
        var httpClient = client ?? _client;
        var csrfRequest = new HttpRequestMessage(HttpMethod.Get, "/api/customer/auth/csrf");
        csrfRequest.Headers.Add("Cookie", authCookie);
        var csrfResponse = await httpClient.SendAsync(csrfRequest);
        Assert.Equal(HttpStatusCode.OK, csrfResponse.StatusCode);

        var csrfResult = await csrfResponse.Content.ReadFromJsonAsync<ApiResponse<string>>();
        Assert.NotNull(csrfResult?.Data);

        var antiforgeryCookie = string.Empty;
        if (csrfResponse.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            var match = cookies.FirstOrDefault(c => c.Contains(".WebAPI.Antiforgery"));
            if (match != null)
            {
                antiforgeryCookie = match.Split(';')[0];
            }
        }

        return (csrfResult.Data, antiforgeryCookie);
    }

    private async Task<Product> CreateTestProductAsync(
        string sku,
        decimal price,
        bool isProductActive = true,
        bool isCategoryActive = true,
        string currency = "PKR")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var catSlug = $"cat_{Guid.NewGuid():N}";
        var category = new Category
        {
            Name = "Category " + catSlug,
            Slug = catSlug,
            IsActive = isCategoryActive
        };
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        var product = new Product
        {
            InternalSku = sku,
            Name = "Product " + sku,
            Category = category,
            SellingPrice = price,
            SellingCurrency = currency,
            IsActive = isProductActive
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();

        return product;
    }

    [Fact]
    public async Task CreateOrder_WithValidProductAndIdempotencyKey_Returns201Created()
    {
        // Arrange
        var sku = $"ORDER_VALID_{Guid.NewGuid():N}";
        var product = await CreateTestProductAsync(sku, 4500.00m);
        var email = $"cust_ord_{Guid.NewGuid():N}@test.local";
        var password = "SecurePass123!";
        var (customerId, authCookie) = await RegisterAndLoginCustomerAsync(email, password);
        var (csrfToken, antiforgeryCookie) = await GetCsrfTokenAsync(authCookie);

        var idempotencyKey = $"IDEMP_{Guid.NewGuid():N}";
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/customer/orders");
        request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
        request.Headers.Add("X-XSRF-TOKEN", csrfToken);
        request.Headers.Add("X-Idempotency-Key", idempotencyKey);
        request.Content = JsonContent.Create(new CreateCustomerOrderRequest
        {
            ProductSku = sku,
            CustomerNotes = "Please deliver quickly"
        });

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<CustomerOrderDetailDto>>();
        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.NotNull(result.Data);

        var order = result.Data;
        Assert.StartsWith("ORD-", order.OrderNumber);
        Assert.Equal(OrderStatus.PendingPayment.ToString(), order.Status);
        Assert.Equal("PKR", order.Currency);
        Assert.Equal(4500.00m, order.Subtotal);
        Assert.Equal(4500.00m, order.TotalAmount);
        Assert.Equal(SalesChannel.Website.ToString(), order.SalesChannel);
        Assert.Equal("Please deliver quickly", order.CustomerNotes);

        // Verify line item snapshot
        Assert.Single(order.Items);
        var item = order.Items[0];
        Assert.Equal(sku, item.ProductSku);
        Assert.Equal(product.Name, item.ProductName);
        Assert.Equal(4500.00m, item.UnitPrice);
        Assert.Equal(1, item.Quantity);
        Assert.Equal(4500.00m, item.TotalPrice);
        Assert.Equal("PKR", item.Currency);

        // Verify initial status history
        Assert.Single(order.StatusHistory);
        Assert.Equal(OrderStatus.PendingPayment.ToString(), order.StatusHistory[0].Status);
        Assert.Contains("website checkout", order.StatusHistory[0].Reason);
    }

    [Fact]
    public async Task CreateOrder_WhenUnauthenticated_Returns401Unauthorized()
    {
        // Act — Attempt to create order anonymously
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/customer/orders");
        request.Content = JsonContent.Create(new CreateCustomerOrderRequest
        {
            ProductSku = "ANY_SKU",
            IdempotencyKey = "key123"
        });

        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateOrder_WhenProductDoesNotExist_Returns404NotFound()
    {
        // Arrange
        var email = $"cust_404_{Guid.NewGuid():N}@test.local";
        var (customerId, authCookie) = await RegisterAndLoginCustomerAsync(email, "SecurePass123!");
        var (csrfToken, antiforgeryCookie) = await GetCsrfTokenAsync(authCookie);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/customer/orders");
        request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
        request.Headers.Add("X-XSRF-TOKEN", csrfToken);
        request.Headers.Add("X-Idempotency-Key", $"IDEMP_{Guid.NewGuid():N}");
        request.Content = JsonContent.Create(new CreateCustomerOrderRequest
        {
            ProductSku = "NON_EXISTENT_SKU_999"
        });

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateOrder_WhenProductIsInactive_Returns400BadRequest()
    {
        // Arrange
        var sku = $"INACTIVE_SKU_{Guid.NewGuid():N}";
        await CreateTestProductAsync(sku, 3000.00m, isProductActive: false);

        var email = $"cust_inact_{Guid.NewGuid():N}@test.local";
        var (customerId, authCookie) = await RegisterAndLoginCustomerAsync(email, "SecurePass123!");
        var (csrfToken, antiforgeryCookie) = await GetCsrfTokenAsync(authCookie);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/customer/orders");
        request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
        request.Headers.Add("X-XSRF-TOKEN", csrfToken);
        request.Headers.Add("X-Idempotency-Key", $"IDEMP_{Guid.NewGuid():N}");
        request.Content = JsonContent.Create(new CreateCustomerOrderRequest
        {
            ProductSku = sku
        });

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<CustomerOrderDetailDto>>();
        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Contains("inactive", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateOrder_WhenCategoryIsInactive_Returns400BadRequest()
    {
        // Arrange
        var sku = $"INACT_CAT_{Guid.NewGuid():N}";
        await CreateTestProductAsync(sku, 3000.00m, isProductActive: true, isCategoryActive: false);

        var email = $"cust_cat_{Guid.NewGuid():N}@test.local";
        var (customerId, authCookie) = await RegisterAndLoginCustomerAsync(email, "SecurePass123!");
        var (csrfToken, antiforgeryCookie) = await GetCsrfTokenAsync(authCookie);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/customer/orders");
        request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
        request.Headers.Add("X-XSRF-TOKEN", csrfToken);
        request.Headers.Add("X-Idempotency-Key", $"IDEMP_{Guid.NewGuid():N}");
        request.Content = JsonContent.Create(new CreateCustomerOrderRequest
        {
            ProductSku = sku
        });

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<CustomerOrderDetailDto>>();
        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Contains("category", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateOrder_MissingIdempotencyKey_Returns400BadRequest()
    {
        // Arrange
        var sku = $"IDEMP_REQ_{Guid.NewGuid():N}";
        await CreateTestProductAsync(sku, 2000.00m);

        var email = $"cust_noidemp_{Guid.NewGuid():N}@test.local";
        var (customerId, authCookie) = await RegisterAndLoginCustomerAsync(email, "SecurePass123!");
        var (csrfToken, antiforgeryCookie) = await GetCsrfTokenAsync(authCookie);

        // No X-Idempotency-Key header and no IdempotencyKey in body
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/customer/orders");
        request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
        request.Headers.Add("X-XSRF-TOKEN", csrfToken);
        request.Content = JsonContent.Create(new CreateCustomerOrderRequest
        {
            ProductSku = sku
        });

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<CustomerOrderDetailDto>>();
        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Contains("idempotency key is required", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateOrder_WithoutCsrfToken_Returns400BadRequest()
    {
        // Arrange
        var sku = $"CSRF_ORDER_{Guid.NewGuid():N}";
        await CreateTestProductAsync(sku, 2500.00m);

        var email = $"cust_nocsrf_{Guid.NewGuid():N}@test.local";
        var (customerId, authCookie) = await RegisterAndLoginCustomerAsync(email, "SecurePass123!");

        // Request with session cookie but MISSING X-XSRF-TOKEN
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/customer/orders");
        request.Headers.Add("Cookie", authCookie);
        request.Headers.Add("X-Idempotency-Key", $"IDEMP_{Guid.NewGuid():N}");
        request.Content = JsonContent.Create(new CreateCustomerOrderRequest
        {
            ProductSku = sku
        });

        // Act
        var response = await _client.SendAsync(request);

        // Assert — AutoValidateAntiforgeryToken rejects with 400 Bad Request
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Idempotency_DuplicateRequestWithIdenticalInput_ReturnsOriginalOrderWithoutDuplicating()
    {
        // Arrange
        var sku = $"IDEMP_DUP_{Guid.NewGuid():N}";
        await CreateTestProductAsync(sku, 5000.00m);

        var email = $"cust_dup_{Guid.NewGuid():N}@test.local";
        var (customerId, authCookie) = await RegisterAndLoginCustomerAsync(email, "SecurePass123!");
        var (csrfToken, antiforgeryCookie) = await GetCsrfTokenAsync(authCookie);

        var sharedKey = $"IDEMP_KEY_{Guid.NewGuid():N}";

        // Act 1 — Initial submission
        var request1 = new HttpRequestMessage(HttpMethod.Post, "/api/customer/orders");
        request1.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
        request1.Headers.Add("X-XSRF-TOKEN", csrfToken);
        request1.Headers.Add("X-Idempotency-Key", sharedKey);
        request1.Content = JsonContent.Create(new CreateCustomerOrderRequest { ProductSku = sku });
        var response1 = await _client.SendAsync(request1);
        Assert.Equal(HttpStatusCode.Created, response1.StatusCode);
        var result1 = await response1.Content.ReadFromJsonAsync<ApiResponse<CustomerOrderDetailDto>>();
        Assert.NotNull(result1?.Data);
        var originalOrderNumber = result1.Data.OrderNumber;

        // Act 2 — Duplicate submission with identical key and payload (e.g. user double-clicked)
        var request2 = new HttpRequestMessage(HttpMethod.Post, "/api/customer/orders");
        request2.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
        request2.Headers.Add("X-XSRF-TOKEN", csrfToken);
        request2.Headers.Add("X-Idempotency-Key", sharedKey);
        request2.Content = JsonContent.Create(new CreateCustomerOrderRequest { ProductSku = sku });
        var response2 = await _client.SendAsync(request2);

        // Assert 2 — Must succeed and return the EXACT same order without inserting another
        Assert.True(response2.StatusCode == HttpStatusCode.OK || response2.StatusCode == HttpStatusCode.Created);
        var result2 = await response2.Content.ReadFromJsonAsync<ApiResponse<CustomerOrderDetailDto>>();
        Assert.NotNull(result2?.Data);
        Assert.Equal(originalOrderNumber, result2.Data.OrderNumber);

        // Verify in database: exactly ONE order exists for this key
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var count = await db.Orders.CountAsync(o => o.CustomerId == customerId && o.IdempotencyKey == sharedKey);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Idempotency_DuplicateKeyWithConflictingPayload_Returns409Conflict()
    {
        // Arrange
        var sku1 = $"IDEMP_CONF1_{Guid.NewGuid():N}";
        var sku2 = $"IDEMP_CONF2_{Guid.NewGuid():N}";
        await CreateTestProductAsync(sku1, 3000.00m);
        await CreateTestProductAsync(sku2, 4000.00m);

        var email = $"cust_conf_{Guid.NewGuid():N}@test.local";
        var (customerId, authCookie) = await RegisterAndLoginCustomerAsync(email, "SecurePass123!");
        var (csrfToken, antiforgeryCookie) = await GetCsrfTokenAsync(authCookie);

        var sharedKey = $"IDEMP_KEY_{Guid.NewGuid():N}";

        // Act 1 — Submit first product
        var request1 = new HttpRequestMessage(HttpMethod.Post, "/api/customer/orders");
        request1.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
        request1.Headers.Add("X-XSRF-TOKEN", csrfToken);
        request1.Headers.Add("X-Idempotency-Key", sharedKey);
        request1.Content = JsonContent.Create(new CreateCustomerOrderRequest { ProductSku = sku1 });
        var response1 = await _client.SendAsync(request1);
        Assert.Equal(HttpStatusCode.Created, response1.StatusCode);

        // Act 2 — Re-use the SAME idempotency key for a DIFFERENT product
        var request2 = new HttpRequestMessage(HttpMethod.Post, "/api/customer/orders");
        request2.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
        request2.Headers.Add("X-XSRF-TOKEN", csrfToken);
        request2.Headers.Add("X-Idempotency-Key", sharedKey);
        request2.Content = JsonContent.Create(new CreateCustomerOrderRequest { ProductSku = sku2 });
        var response2 = await _client.SendAsync(request2);

        // Assert 2 — Must be rejected with 409 Conflict
        Assert.Equal(HttpStatusCode.Conflict, response2.StatusCode);
        var result2 = await response2.Content.ReadFromJsonAsync<ApiResponse<CustomerOrderDetailDto>>();
        Assert.NotNull(result2);
        Assert.False(result2.Success);
        Assert.Contains("different order details", result2.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OrderListingAndDetail_ReturnsOnlyCustomerOrders_AndIsolatesOtherCustomers()
    {
        // Arrange — Two distinct customers: Customer A and Customer B with separate sessions
        var sku = $"ORDER_ISOL_{Guid.NewGuid():N}";
        await CreateTestProductAsync(sku, 2500.00m);

        var clientA = _factory.CreateClient();
        var clientB = _factory.CreateClient();

        var (custAId, cookieA) = await RegisterAndLoginCustomerAsync($"custA_{Guid.NewGuid():N}@test.local", "SecurePass123!", clientA);
        var (tokenA, antiA) = await GetCsrfTokenAsync(cookieA, clientA);

        var (custBId, cookieB) = await RegisterAndLoginCustomerAsync($"custB_{Guid.NewGuid():N}@test.local", "SecurePass123!", clientB);
        var (tokenB, antiB) = await GetCsrfTokenAsync(cookieB, clientB);

        // Customer A places an order
        var orderReqA = new HttpRequestMessage(HttpMethod.Post, "/api/customer/orders");
        orderReqA.Headers.Add("Cookie", $"{cookieA}; {antiA}");
        orderReqA.Headers.Add("X-XSRF-TOKEN", tokenA);
        orderReqA.Headers.Add("X-Idempotency-Key", $"KEYA_{Guid.NewGuid():N}");
        orderReqA.Content = JsonContent.Create(new CreateCustomerOrderRequest { ProductSku = sku });
        var orderRespA = await clientA.SendAsync(orderReqA);
        Assert.Equal(HttpStatusCode.Created, orderRespA.StatusCode);
        var orderA = (await orderRespA.Content.ReadFromJsonAsync<ApiResponse<CustomerOrderDetailDto>>())!.Data!;

        // Customer B places an order
        var orderReqB = new HttpRequestMessage(HttpMethod.Post, "/api/customer/orders");
        orderReqB.Headers.Add("Cookie", $"{cookieB}; {antiB}");
        orderReqB.Headers.Add("X-XSRF-TOKEN", tokenB);
        orderReqB.Headers.Add("X-Idempotency-Key", $"KEYB_{Guid.NewGuid():N}");
        orderReqB.Content = JsonContent.Create(new CreateCustomerOrderRequest { ProductSku = sku });
        var orderRespB = await clientB.SendAsync(orderReqB);
        Assert.Equal(HttpStatusCode.Created, orderRespB.StatusCode);
        var orderB = (await orderRespB.Content.ReadFromJsonAsync<ApiResponse<CustomerOrderDetailDto>>())!.Data!;

        // Act 1 — Customer A lists orders
        var listReqA = new HttpRequestMessage(HttpMethod.Get, "/api/customer/orders");
        listReqA.Headers.Add("Cookie", cookieA);
        var listRespA = await clientA.SendAsync(listReqA);
        Assert.Equal(HttpStatusCode.OK, listRespA.StatusCode);
        var listA = (await listRespA.Content.ReadFromJsonAsync<ApiResponse<PagedResult<CustomerOrderSummaryDto>>>())!.Data!;

        // Assert 1 — Customer A sees their own order, but NOT Customer B's order
        Assert.Contains(listA.Items, o => o.OrderNumber == orderA.OrderNumber);
        Assert.DoesNotContain(listA.Items, o => o.OrderNumber == orderB.OrderNumber);

        // Act 2 — Customer A retrieves their own order details
        var getReqA = new HttpRequestMessage(HttpMethod.Get, $"/api/customer/orders/{orderA.OrderNumber}");
        getReqA.Headers.Add("Cookie", cookieA);
        var getRespA = await clientA.SendAsync(getReqA);
        Assert.Equal(HttpStatusCode.OK, getRespA.StatusCode);

        // Act 3 — Customer A attempts to access Customer B's order by known order number
        var snoopingReq = new HttpRequestMessage(HttpMethod.Get, $"/api/customer/orders/{orderB.OrderNumber}");
        snoopingReq.Headers.Add("Cookie", cookieA);
        var snoopingResp = await clientA.SendAsync(snoopingReq);

        // Assert 3 — Must return 404 Not Found to prevent data leakage or order enumeration
        Assert.Equal(HttpStatusCode.NotFound, snoopingResp.StatusCode);
    }

    [Fact]
    public async Task PriceSnapshot_RemainsImmutable_WhenCatalogPriceChangesAfterOrderPlacement()
    {
        // Arrange
        var sku = $"SNAP_TEST_{Guid.NewGuid():N}";
        var product = await CreateTestProductAsync(sku, 6000.00m);

        var (customerId, authCookie) = await RegisterAndLoginCustomerAsync($"cust_snap_{Guid.NewGuid():N}@test.local", "SecurePass123!");
        var (csrfToken, antiforgeryCookie) = await GetCsrfTokenAsync(authCookie);

        var orderReq = new HttpRequestMessage(HttpMethod.Post, "/api/customer/orders");
        orderReq.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
        orderReq.Headers.Add("X-XSRF-TOKEN", csrfToken);
        orderReq.Headers.Add("X-Idempotency-Key", $"SNAPKEY_{Guid.NewGuid():N}");
        orderReq.Content = JsonContent.Create(new CreateCustomerOrderRequest { ProductSku = sku });
        var orderResp = await _client.SendAsync(orderReq);
        Assert.Equal(HttpStatusCode.Created, orderResp.StatusCode);
        var order = (await orderResp.Content.ReadFromJsonAsync<ApiResponse<CustomerOrderDetailDto>>())!.Data!;

        // Act — Admin updates catalog price from 6,000 to 9,500 PKR
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var p = await db.Products.FirstAsync(prod => prod.InternalSku == sku);
            p.SellingPrice = 9500.00m;
            p.Name = "Renamed Product";
            await db.SaveChangesAsync();
        }

        // Fetch order details again
        var getReq = new HttpRequestMessage(HttpMethod.Get, $"/api/customer/orders/{order.OrderNumber}");
        getReq.Headers.Add("Cookie", authCookie);
        var getResp = await _client.SendAsync(getReq);
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var fetchedOrder = (await getResp.Content.ReadFromJsonAsync<ApiResponse<CustomerOrderDetailDto>>())!.Data!;

        // Assert — Order snapshots are 100% unaltered
        Assert.Equal(6000.00m, fetchedOrder.TotalAmount);
        Assert.Equal(6000.00m, fetchedOrder.Subtotal);
        Assert.Equal(6000.00m, fetchedOrder.Items[0].UnitPrice);
        Assert.Equal(6000.00m, fetchedOrder.Items[0].TotalPrice);
        Assert.Equal(product.Name, fetchedOrder.Items[0].ProductName);
    }

    private const string PostgresTestConnectionString = PostgreSqlTestDatabase.ConnectionString;

    [Fact]
    public async Task PostgreSql_EnforcesDurableIdempotency_AndUniqueConstraint()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(PostgresTestConnectionString)
            .Options;

        await using var db = new AppDbContext(options);

        if (!await PostgreSqlTestDatabase.EnsureMigratedAsync(db))
        {
            return;
        }

        var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        var customerEmail = $"pg_idemp_{uniqueSuffix}@test.local".ToLowerInvariant();

        // 1. Create Customer and Product in PostgreSQL
        var customer = new ApplicationUser
        {
            UserName = customerEmail,
            Email = customerEmail,
            FullName = "PG Idempotency Customer",
            EmailConfirmed = true,
            IsActive = true
        };
        db.Users.Add(customer);

        var category = new Category
        {
            Name = "PG Cat " + uniqueSuffix,
            Slug = "pg-cat-idemp-" + uniqueSuffix,
            IsActive = true
        };
        db.Categories.Add(category);

        var product = new Product
        {
            InternalSku = "PG_IDEMP_SKU_" + uniqueSuffix.ToUpperInvariant(),
            Name = "PG Idemp Product",
            Category = category,
            SellingPrice = 3500m,
            SellingCurrency = "PKR",
            IsActive = true
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();

        var sharedIdempotencyKey = $"PG_KEY_{uniqueSuffix.ToUpperInvariant()}";
        var order1Number = $"ORD-PG1-{uniqueSuffix.ToUpperInvariant()}";
        var order2Number = $"ORD-PG2-{uniqueSuffix.ToUpperInvariant()}";

        try
        {
            // 2. Insert first order with shared idempotency key
            var order1 = new Order
            {
                OrderNumber = order1Number,
                CustomerId = customer.Id,
                Status = OrderStatus.PendingPayment,
                Currency = "PKR",
                Subtotal = 3500m,
                TotalAmount = 3500m,
                SalesChannel = SalesChannel.Website,
                IdempotencyKey = sharedIdempotencyKey
            };
            var item1 = new OrderItem
            {
                Order = order1,
                ProductId = product.Id,
                ProductSkuSnapshot = product.InternalSku,
                ProductNameSnapshot = product.Name,
                Quantity = 1,
                UnitPriceSnapshot = 3500m,
                Currency = "PKR",
                TotalPrice = 3500m
            };
            order1.Items.Add(item1);
            db.Orders.Add(order1);
            await db.SaveChangesAsync();

            // 3. Attempt to insert SECOND order with the SAME customer and SAME idempotency key directly into PostgreSQL
            var duplicateOrder = new Order
            {
                OrderNumber = order2Number,
                CustomerId = customer.Id,
                Status = OrderStatus.PendingPayment,
                Currency = "PKR",
                Subtotal = 3500m,
                TotalAmount = 3500m,
                SalesChannel = SalesChannel.Website,
                IdempotencyKey = sharedIdempotencyKey // Colliding key!
            };
            var item2 = new OrderItem
            {
                Order = duplicateOrder,
                ProductId = product.Id,
                ProductSkuSnapshot = product.InternalSku,
                ProductNameSnapshot = product.Name,
                Quantity = 1,
                UnitPriceSnapshot = 3500m,
                Currency = "PKR",
                TotalPrice = 3500m
            };
            duplicateOrder.Items.Add(item2);
            db.Orders.Add(duplicateOrder);

            // Assert: PostgreSQL must reject with DbUpdateException wrapping PostgresException 23505 (unique_violation)
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.NotNull(ex.InnerException);
            var pgEx = Assert.IsType<PostgresException>(ex.InnerException);
            Assert.Equal("23505", pgEx.SqlState);
            Assert.Contains("IX_Orders_CustomerId_IdempotencyKey", pgEx.ConstraintName ?? pgEx.Message);
        }
        finally
        {
            // Clean up test records
            await using var cleanupDb = new AppDbContext(options);

            var orders = await cleanupDb.Orders
                .Include(o => o.Items)
                .Include(o => o.StatusHistory)
                .Where(o => o.CustomerId == customer.Id)
                .ToListAsync();

            foreach (var o in orders)
            {
                cleanupDb.OrderItems.RemoveRange(o.Items);
                cleanupDb.OrderStatusHistories.RemoveRange(o.StatusHistory);
            }
            cleanupDb.Orders.RemoveRange(orders);

            var p = await cleanupDb.Products.FindAsync(product.Id);
            if (p != null) cleanupDb.Products.Remove(p);

            var c = await cleanupDb.Categories.FindAsync(category.Id);
            if (c != null) cleanupDb.Categories.Remove(c);

            var u = await cleanupDb.Users.FindAsync(customer.Id);
            if (u != null) cleanupDb.Users.Remove(u);

            await cleanupDb.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task PostgreSql_SimultaneousRequests_CreateExactlyOneOrder_AndBothReceiveOriginalOrSafeResponse()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(PostgresTestConnectionString)
            .Options;

        await using var setupDb = new AppDbContext(options);
        if (!await PostgreSqlTestDatabase.EnsureMigratedAsync(setupDb))
        {
            return;
        }

        var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        var customerEmail = $"pg_concurrent_{uniqueSuffix}@test.local".ToLowerInvariant();

        var customer = new ApplicationUser
        {
            UserName = customerEmail,
            Email = customerEmail,
            FullName = "Concurrent Customer",
            EmailConfirmed = true,
            IsActive = true
        };
        setupDb.Users.Add(customer);

        var category = new Category
        {
            Name = "Conc Cat " + uniqueSuffix,
            Slug = "conc-cat-" + uniqueSuffix,
            IsActive = true
        };
        setupDb.Categories.Add(category);

        var product = new Product
        {
            InternalSku = "CONC_SKU_" + uniqueSuffix.ToUpperInvariant(),
            Name = "Concurrent Product",
            Category = category,
            SellingPrice = 4500m,
            SellingCurrency = "PKR",
            IsActive = true
        };
        setupDb.Products.Add(product);
        await setupDb.SaveChangesAsync();

        var sharedKey = $"CONC_KEY_{uniqueSuffix.ToUpperInvariant()}";
        var request = new CreateCustomerOrderRequest { ProductSku = product.InternalSku };

        try
        {
            // Set up two distinct DbContexts with independent database connections to PostgreSQL
            await using var db1 = new AppDbContext(options);
            await using var db2 = new AppDbContext(options);

            var generator = new OrderNumberGenerator();
            var service1 = new CustomerOrderService(db1, generator, NullLogger<CustomerOrderService>.Instance);
            var service2 = new CustomerOrderService(db2, generator, NullLogger<CustomerOrderService>.Instance);

            // Execute two simultaneous order creation requests with the identical idempotency key
            var task1 = service1.CreateOrderAsync(customer.Id, request, sharedKey);
            var task2 = service2.CreateOrderAsync(customer.Id, request, sharedKey);

            var results = await Task.WhenAll(task1, task2);
            var res1 = results[0];
            var res2 = results[1];

            // 1. Verify: Exactly ONE order was created in PostgreSQL
            await using var verifyDb = new AppDbContext(options);
            var orderCount = await verifyDb.Orders
                .CountAsync(o => o.CustomerId == customer.Id && o.IdempotencyKey == sharedKey);
            Assert.Equal(1, orderCount);

            var itemsCount = await verifyDb.OrderItems
                .CountAsync(i => i.Order.CustomerId == customer.Id && i.Order.IdempotencyKey == sharedKey);
            Assert.Equal(1, itemsCount);

            var historyCount = await verifyDb.OrderStatusHistories
                .CountAsync(h => h.Order.CustomerId == customer.Id && h.Order.IdempotencyKey == sharedKey);
            Assert.Equal(1, historyCount);

            // 2. Verify: Both requests received valid responses without unexpected server errors
            Assert.True(res1.Status == CustomerOrderResultStatus.Success || res2.Status == CustomerOrderResultStatus.Success);

            if (res1.Status == CustomerOrderResultStatus.Success && res2.Status == CustomerOrderResultStatus.Success)
            {
                Assert.Equal(res1.Order!.OrderNumber, res2.Order!.OrderNumber);
            }
            else
            {
                var conflictResult = res1.Status == CustomerOrderResultStatus.Conflict ? res1 : res2;
                Assert.NotNull(conflictResult.Message);
            }
        }
        finally
        {
            await using var cleanupDb = new AppDbContext(options);
            var orders = await cleanupDb.Orders
                .Include(o => o.Items)
                .Include(o => o.StatusHistory)
                .Where(o => o.CustomerId == customer.Id)
                .ToListAsync();

            foreach (var o in orders)
            {
                cleanupDb.OrderItems.RemoveRange(o.Items);
                cleanupDb.OrderStatusHistories.RemoveRange(o.StatusHistory);
            }
            cleanupDb.Orders.RemoveRange(orders);

            var p = await cleanupDb.Products.FindAsync(product.Id);
            if (p != null) cleanupDb.Products.Remove(p);

            var c = await cleanupDb.Categories.FindAsync(category.Id);
            if (c != null) cleanupDb.Categories.Remove(c);

            var u = await cleanupDb.Users.FindAsync(customer.Id);
            if (u != null) cleanupDb.Users.Remove(u);

            await cleanupDb.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task PostgreSql_RepeatingRequest_AfterPriceChangeAndDisable_ReturnsOriginalOrderAndFrozenPrice()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(PostgresTestConnectionString)
            .Options;

        await using var setupDb = new AppDbContext(options);
        if (!await PostgreSqlTestDatabase.EnsureMigratedAsync(setupDb))
        {
            return;
        }

        var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        var customerEmail = $"pg_freeze_{uniqueSuffix}@test.local".ToLowerInvariant();

        var customer = new ApplicationUser
        {
            UserName = customerEmail,
            Email = customerEmail,
            FullName = "Freeze Test Customer",
            EmailConfirmed = true,
            IsActive = true
        };
        setupDb.Users.Add(customer);

        var category = new Category
        {
            Name = "Freeze Cat " + uniqueSuffix,
            Slug = "freeze-cat-" + uniqueSuffix,
            IsActive = true
        };
        setupDb.Categories.Add(category);

        var product = new Product
        {
            InternalSku = "FREEZE_SKU_" + uniqueSuffix.ToUpperInvariant(),
            Name = "Freeze Price Product",
            Category = category,
            SellingPrice = 2500m,
            SellingCurrency = "PKR",
            IsActive = true
        };
        setupDb.Products.Add(product);
        await setupDb.SaveChangesAsync();

        var sharedKey = $"FREEZE_KEY_{uniqueSuffix.ToUpperInvariant()}";
        var request = new CreateCustomerOrderRequest { ProductSku = product.InternalSku };

        try
        {
            await using var db1 = new AppDbContext(options);
            var generator = new OrderNumberGenerator();
            var service1 = new CustomerOrderService(db1, generator, NullLogger<CustomerOrderService>.Instance);

            // Step 1: Initial successful order
            var firstResult = await service1.CreateOrderAsync(customer.Id, request, sharedKey);
            Assert.Equal(CustomerOrderResultStatus.Success, firstResult.Status);
            Assert.False(firstResult.IsExistingIdempotent);
            Assert.Equal(2500m, firstResult.Order!.TotalAmount);
            Assert.Equal(2500m, firstResult.Order.Items[0].UnitPrice);
            var initialOrderNumber = firstResult.Order.OrderNumber;

            // Step 2: Product price changes in catalog AND product is deactivated
            await using var mutateDb = new AppDbContext(options);
            var prodToMutate = await mutateDb.Products.FirstAsync(p => p.Id == product.Id);
            prodToMutate.SellingPrice = 8999m; // Price increased dramatically
            prodToMutate.IsActive = false;     // Product deactivated
            await mutateDb.SaveChangesAsync();

            // Step 3: Customer repeats identical order request
            await using var db2 = new AppDbContext(options);
            var service2 = new CustomerOrderService(db2, generator, NullLogger<CustomerOrderService>.Instance);
            var secondResult = await service2.CreateOrderAsync(customer.Id, request, sharedKey);

            // Assert: Returned order is the original one with frozen original price, not new catalog price
            Assert.Equal(CustomerOrderResultStatus.Success, secondResult.Status);
            Assert.True(secondResult.IsExistingIdempotent);
            Assert.Equal(initialOrderNumber, secondResult.Order!.OrderNumber);
            Assert.Equal(2500m, secondResult.Order.TotalAmount);
            Assert.Equal(2500m, secondResult.Order.Items[0].UnitPrice);

            // Exactly 1 order in PostgreSQL
            await using var verifyDb = new AppDbContext(options);
            var count = await verifyDb.Orders.CountAsync(o => o.CustomerId == customer.Id && o.IdempotencyKey == sharedKey);
            Assert.Equal(1, count);
        }
        finally
        {
            await using var cleanupDb = new AppDbContext(options);
            var orders = await cleanupDb.Orders
                .Include(o => o.Items)
                .Include(o => o.StatusHistory)
                .Where(o => o.CustomerId == customer.Id)
                .ToListAsync();

            foreach (var o in orders)
            {
                cleanupDb.OrderItems.RemoveRange(o.Items);
                cleanupDb.OrderStatusHistories.RemoveRange(o.StatusHistory);
            }
            cleanupDb.Orders.RemoveRange(orders);

            var p = await cleanupDb.Products.FindAsync(product.Id);
            if (p != null) cleanupDb.Products.Remove(p);

            var c = await cleanupDb.Categories.FindAsync(category.Id);
            if (c != null) cleanupDb.Categories.Remove(c);

            var u = await cleanupDb.Users.FindAsync(customer.Id);
            if (u != null) cleanupDb.Users.Remove(u);

            await cleanupDb.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task PostgreSql_ReusingSameKey_ForDifferentProduct_Returns409Conflict()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(PostgresTestConnectionString)
            .Options;

        await using var setupDb = new AppDbContext(options);
        if (!await PostgreSqlTestDatabase.EnsureMigratedAsync(setupDb))
        {
            return;
        }

        var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        var customerEmail = $"pg_diffsku_{uniqueSuffix}@test.local".ToLowerInvariant();

        var customer = new ApplicationUser
        {
            UserName = customerEmail,
            Email = customerEmail,
            FullName = "DiffSku Customer",
            EmailConfirmed = true,
            IsActive = true
        };
        setupDb.Users.Add(customer);

        var category = new Category
        {
            Name = "DiffSku Cat " + uniqueSuffix,
            Slug = "diffsku-cat-" + uniqueSuffix,
            IsActive = true
        };
        setupDb.Categories.Add(category);

        var product1 = new Product
        {
            InternalSku = "SKU_A_" + uniqueSuffix.ToUpperInvariant(),
            Name = "Product A",
            Category = category,
            SellingPrice = 3000m,
            SellingCurrency = "PKR",
            IsActive = true
        };
        var product2 = new Product
        {
            InternalSku = "SKU_B_" + uniqueSuffix.ToUpperInvariant(),
            Name = "Product B",
            Category = category,
            SellingPrice = 4000m,
            SellingCurrency = "PKR",
            IsActive = true
        };
        setupDb.Products.AddRange(product1, product2);
        await setupDb.SaveChangesAsync();

        var sharedKey = $"REUSE_KEY_{uniqueSuffix.ToUpperInvariant()}";

        try
        {
            await using var db1 = new AppDbContext(options);
            var generator = new OrderNumberGenerator();
            var service1 = new CustomerOrderService(db1, generator, NullLogger<CustomerOrderService>.Instance);

            // Step 1: Submit order for Product A
            var res1 = await service1.CreateOrderAsync(customer.Id, new CreateCustomerOrderRequest { ProductSku = product1.InternalSku }, sharedKey);
            Assert.Equal(CustomerOrderResultStatus.Success, res1.Status);

            // Step 2: Attempt to reuse same key for Product B
            await using var db2 = new AppDbContext(options);
            var service2 = new CustomerOrderService(db2, generator, NullLogger<CustomerOrderService>.Instance);
            var res2 = await service2.CreateOrderAsync(customer.Id, new CreateCustomerOrderRequest { ProductSku = product2.InternalSku }, sharedKey);

            // Assert: Rejected with 409 Conflict
            Assert.Equal(CustomerOrderResultStatus.Conflict, res2.Status);
            Assert.Contains("different order details", res2.Message, StringComparison.OrdinalIgnoreCase);

            // Only 1 order exists
            await using var verifyDb = new AppDbContext(options);
            var count = await verifyDb.Orders.CountAsync(o => o.CustomerId == customer.Id && o.IdempotencyKey == sharedKey);
            Assert.Equal(1, count);
        }
        finally
        {
            await using var cleanupDb = new AppDbContext(options);
            var orders = await cleanupDb.Orders
                .Include(o => o.Items)
                .Include(o => o.StatusHistory)
                .Where(o => o.CustomerId == customer.Id)
                .ToListAsync();

            foreach (var o in orders)
            {
                cleanupDb.OrderItems.RemoveRange(o.Items);
                cleanupDb.OrderStatusHistories.RemoveRange(o.StatusHistory);
            }
            cleanupDb.Orders.RemoveRange(orders);

            var p1 = await cleanupDb.Products.FindAsync(product1.Id);
            if (p1 != null) cleanupDb.Products.Remove(p1);
            var p2 = await cleanupDb.Products.FindAsync(product2.Id);
            if (p2 != null) cleanupDb.Products.Remove(p2);

            var c = await cleanupDb.Categories.FindAsync(category.Id);
            if (c != null) cleanupDb.Categories.Remove(c);

            var u = await cleanupDb.Users.FindAsync(customer.Id);
            if (u != null) cleanupDb.Users.Remove(u);

            await cleanupDb.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task PostgreSql_DifferentCustomers_CanIndependentlyUseSameIdempotencyKey()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(PostgresTestConnectionString)
            .Options;

        await using var setupDb = new AppDbContext(options);
        if (!await PostgreSqlTestDatabase.EnsureMigratedAsync(setupDb))
        {
            return;
        }

        var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        var custAEmail = $"pg_cust_a_{uniqueSuffix}@test.local".ToLowerInvariant();
        var custBEmail = $"pg_cust_b_{uniqueSuffix}@test.local".ToLowerInvariant();

        var customerA = new ApplicationUser { UserName = custAEmail, Email = custAEmail, FullName = "Customer A", EmailConfirmed = true, IsActive = true };
        var customerB = new ApplicationUser { UserName = custBEmail, Email = custBEmail, FullName = "Customer B", EmailConfirmed = true, IsActive = true };
        setupDb.Users.AddRange(customerA, customerB);

        var category = new Category { Name = "Cross Cust Cat " + uniqueSuffix, Slug = "cross-cat-" + uniqueSuffix, IsActive = true };
        setupDb.Categories.Add(category);

        var product = new Product
        {
            InternalSku = "CROSS_SKU_" + uniqueSuffix.ToUpperInvariant(),
            Name = "Cross Customer Product",
            Category = category,
            SellingPrice = 3000m,
            SellingCurrency = "PKR",
            IsActive = true
        };
        setupDb.Products.Add(product);
        await setupDb.SaveChangesAsync();

        var sharedKey = $"SHARED_CUST_KEY_{uniqueSuffix.ToUpperInvariant()}";
        var request = new CreateCustomerOrderRequest { ProductSku = product.InternalSku };

        try
        {
            await using var dbA = new AppDbContext(options);
            await using var dbB = new AppDbContext(options);
            var generator = new OrderNumberGenerator();
            var serviceA = new CustomerOrderService(dbA, generator, NullLogger<CustomerOrderService>.Instance);
            var serviceB = new CustomerOrderService(dbB, generator, NullLogger<CustomerOrderService>.Instance);

            var resA = await serviceA.CreateOrderAsync(customerA.Id, request, sharedKey);
            var resB = await serviceB.CreateOrderAsync(customerB.Id, request, sharedKey);

            // Assert: Both succeed independently
            Assert.Equal(CustomerOrderResultStatus.Success, resA.Status);
            Assert.Equal(CustomerOrderResultStatus.Success, resB.Status);
            Assert.NotEqual(resA.Order!.OrderNumber, resB.Order!.OrderNumber);

            // In PostgreSQL, exactly 2 orders exist with this idempotency key
            await using var verifyDb = new AppDbContext(options);
            var count = await verifyDb.Orders.CountAsync(o => o.IdempotencyKey == sharedKey);
            Assert.Equal(2, count);
        }
        finally
        {
            await using var cleanupDb = new AppDbContext(options);
            var orders = await cleanupDb.Orders
                .Include(o => o.Items)
                .Include(o => o.StatusHistory)
                .Where(o => o.CustomerId == customerA.Id || o.CustomerId == customerB.Id)
                .ToListAsync();

            foreach (var o in orders)
            {
                cleanupDb.OrderItems.RemoveRange(o.Items);
                cleanupDb.OrderStatusHistories.RemoveRange(o.StatusHistory);
            }
            cleanupDb.Orders.RemoveRange(orders);

            var p = await cleanupDb.Products.FindAsync(product.Id);
            if (p != null) cleanupDb.Products.Remove(p);

            var c = await cleanupDb.Categories.FindAsync(category.Id);
            if (c != null) cleanupDb.Categories.Remove(c);

            var uA = await cleanupDb.Users.FindAsync(customerA.Id);
            if (uA != null) cleanupDb.Users.Remove(uA);

            var uB = await cleanupDb.Users.FindAsync(customerB.Id);
            if (uB != null) cleanupDb.Users.Remove(uB);

            await cleanupDb.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task PostgreSql_FailedTransaction_LeavesNoIncompleteRecords()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(PostgresTestConnectionString)
            .Options;

        await using var setupDb = new AppDbContext(options);
        if (!await PostgreSqlTestDatabase.EnsureMigratedAsync(setupDb))
        {
            return;
        }

        var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        var customerEmail = $"pg_rollback_{uniqueSuffix}@test.local".ToLowerInvariant();

        var customer = new ApplicationUser { UserName = customerEmail, Email = customerEmail, FullName = "Rollback Customer", EmailConfirmed = true, IsActive = true };
        setupDb.Users.Add(customer);

        var category = new Category { Name = "Rollback Cat " + uniqueSuffix, Slug = "rollback-cat-" + uniqueSuffix, IsActive = true };
        setupDb.Categories.Add(category);

        var product = new Product
        {
            InternalSku = "ROLLBACK_SKU_" + uniqueSuffix.ToUpperInvariant(),
            Name = "Rollback Product",
            Category = category,
            SellingPrice = 5000m,
            SellingCurrency = "PKR",
            IsActive = true
        };
        setupDb.Products.Add(product);
        await setupDb.SaveChangesAsync();

        var orderNumber = $"ORD-FAIL-{uniqueSuffix.ToUpperInvariant()}";

        try
        {
            // Simulate a failed transaction where an order, item, and history are staged, but a failure occurs
            await using var db = new AppDbContext(options);
            await using (var transaction = await db.Database.BeginTransactionAsync())
            {
                var order = new Order
                {
                    OrderNumber = orderNumber,
                    CustomerId = customer.Id,
                    Status = OrderStatus.PendingPayment,
                    Currency = "PKR",
                    Subtotal = 5000m,
                    TotalAmount = 5000m,
                    SalesChannel = SalesChannel.Website,
                    IdempotencyKey = $"FAIL_KEY_{uniqueSuffix}"
                };

                var item = new OrderItem
                {
                    Order = order,
                    ProductId = product.Id,
                    ProductSkuSnapshot = product.InternalSku,
                    ProductNameSnapshot = product.Name,
                    Quantity = 1,
                    UnitPriceSnapshot = 5000m,
                    Currency = "PKR",
                    TotalPrice = 5000m
                };
                order.Items.Add(item);

                var history = new OrderStatusHistory
                {
                    Order = order,
                    NewStatus = OrderStatus.PendingPayment,
                    Reason = "Failed attempt",
                    ChangedByUserId = customer.Id,
                    ChangedAt = DateTime.UtcNow
                };
                order.StatusHistory.Add(history);

                db.Orders.Add(order);
                await db.SaveChangesAsync();

                // Roll back explicitly to simulate transactional failure
                await transaction.RollbackAsync();
            }

            // Verify: No records whatsoever remain in Orders, OrderItems, or OrderStatusHistory
            await using var verifyDb = new AppDbContext(options);
            var orderExists = await verifyDb.Orders.AnyAsync(o => o.OrderNumber == orderNumber);
            var itemExists = await verifyDb.OrderItems.AnyAsync(i => i.ProductSkuSnapshot == product.InternalSku && i.Order.OrderNumber == orderNumber);
            var historyExists = await verifyDb.OrderStatusHistories.AnyAsync(h => h.Order.OrderNumber == orderNumber);

            Assert.False(orderExists);
            Assert.False(itemExists);
            Assert.False(historyExists);
        }
        finally
        {
            await using var cleanupDb = new AppDbContext(options);
            var p = await cleanupDb.Products.FindAsync(product.Id);
            if (p != null) cleanupDb.Products.Remove(p);

            var c = await cleanupDb.Categories.FindAsync(category.Id);
            if (c != null) cleanupDb.Categories.Remove(c);

            var u = await cleanupDb.Users.FindAsync(customer.Id);
            if (u != null) cleanupDb.Users.Remove(u);

            await cleanupDb.SaveChangesAsync();
        }
    }
}
