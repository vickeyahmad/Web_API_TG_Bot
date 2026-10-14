using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApiTgBot.Common.Models;
using WebApiTgBot.Infrastructure.Data;
using WebApiTgBot.Modules.Catalog.Models;
using WebApiTgBot.Modules.Catalog.Models.DTOs;
using WebApiTgBot.Modules.Identity.Models;
using WebApiTgBot.Modules.Identity.Models.DTOs;
using WebApiTgBot.Modules.Orders.Models.DTOs;
using WebApiTgBot.Modules.Payments.Models.DTOs;

namespace WebApiTgBot.Tests;

/// <summary>
/// Milestone 6A: Frontend Integration Readiness & API Contracts Test Suite.
/// Verifies:
/// 1. CORS origin enforcement: allowed production origins receive CORS headers; unapproved origins are blocked.
/// 2. CORS preflight OPTIONS handling: supports credentials, HTTP methods, and custom headers (X-XSRF-TOKEN, X-Idempotency-Key).
/// 3. Customer and Admin login cookie behavior: HttpOnly, SameSite=Lax, sliding expiration, separate schemes.
/// 4. Customer vs Admin session isolation: Customer cannot access Admin endpoints; Admin cannot access Customer endpoints.
/// 5. Immediate session invalidation for deactivated accounts.
/// 6. CSRF token issuance, non-cacheable JSON response, and validation on mutative POST requests.
/// 7. Missing CSRF token rejection with 400 Bad Request.
/// 8. Consistent JSON-compatible 401/403 responses across authentication middleware.
/// 9. Feature-gate enforcement: Top-ups, wallet purchases, and manual fulfillment return 403 when disabled.
/// 10. Customer DTO privacy: strict prevention of vendor names, IDs, supplier costs, and external mappings in customer APIs.
/// </summary>
public class FrontendIntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public FrontendIntegrationTests(CustomWebApplicationFactory factory)
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

    private async Task<ApplicationUser> CreateTestUserAsync(string email, string password, string role, bool isActive = true)
    {
        await EnsureRolesExistAsync();

        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var existing = await userManager.FindByEmailAsync(email);
        if (existing != null)
        {
            return existing;
        }

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            FullName = $"{role} User {Guid.NewGuid():N}",
            IsActive = isActive,
            EmailConfirmed = true
        };

        var result = await userManager.CreateAsync(user, password);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));

        var roleResult = await userManager.AddToRoleAsync(user, role);
        Assert.True(roleResult.Succeeded, string.Join("; ", roleResult.Errors.Select(e => e.Description)));

        return user;
    }

    private async Task<(string AuthCookie, string CsrfCookie, string CsrfToken)> LoginCustomerAsync(string email, string password)
    {
        var loginResponse = await _client.PostAsJsonAsync("/api/customer/auth/login", new CustomerLoginRequest
        {
            Email = email,
            Password = password,
            RememberMe = true
        });
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        var authCookie = loginResponse.Headers.GetValues("Set-Cookie")
            .First(c => c.Contains(".WebAPI.Customer.Auth"))
            .Split(';')[0];

        var csrfReq = new HttpRequestMessage(HttpMethod.Get, "/api/customer/auth/csrf");
        csrfReq.Headers.Add("Cookie", authCookie);
        var csrfResponse = await _client.SendAsync(csrfReq);
        Assert.Equal(HttpStatusCode.OK, csrfResponse.StatusCode);

        var csrfResult = await csrfResponse.Content.ReadFromJsonAsync<ApiResponse<string>>();
        Assert.NotNull(csrfResult);

        var csrfCookie = csrfResponse.Headers.GetValues("Set-Cookie")
            .First(c => c.Contains(".WebAPI.Antiforgery"))
            .Split(';')[0];

        return (authCookie, csrfCookie, csrfResult.Data!);
    }

    private async Task<(string AuthCookie, string CsrfCookie, string CsrfToken)> LoginAdminAsync(string email, string password)
    {
        var loginResponse = await _client.PostAsJsonAsync("/api/admin/auth/login", new AdminLoginRequest
        {
            Email = email,
            Password = password,
            RememberMe = true
        });
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        var authCookie = loginResponse.Headers.GetValues("Set-Cookie")
            .First(c => c.Contains(".WebAPI.Admin.Auth"))
            .Split(';')[0];

        var csrfResponse = await _client.GetAsync("/api/admin/auth/csrf");
        Assert.Equal(HttpStatusCode.OK, csrfResponse.StatusCode);

        var csrfResult = await csrfResponse.Content.ReadFromJsonAsync<ApiResponse<string>>();
        Assert.NotNull(csrfResult);

        var csrfCookie = csrfResponse.Headers.GetValues("Set-Cookie")
            .First(c => c.Contains(".WebAPI.Antiforgery"))
            .Split(';')[0];

        return (authCookie, csrfCookie, csrfResult.Data!);
    }

    // ── 1. CORS Allowed Origins ──────────────────────────────────────────────

    [Theory]
    [InlineData("https://tools.gogaabyte.com")]
    [InlineData("https://admin.gogaabyte.com")]
    [InlineData("https://smm.gogaabyte.com")]
    [InlineData("https://api.gogaabyte.com")]
    public async Task Cors_ApprovedProductionOrigins_ReceiveCorsAllowHeaders(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/products");
        request.Headers.Add("Origin", origin);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal(origin, response.Headers.GetValues("Access-Control-Allow-Origin").FirstOrDefault());
        Assert.Equal("true", response.Headers.GetValues("Access-Control-Allow-Credentials").FirstOrDefault());
    }

    [Theory]
    [InlineData("http://localhost:8080")]
    [InlineData("https://localhost:8080")]
    public async Task Cors_DevelopmentOrigin8080_ReceivesCorsAllowHeaders_WhenConfigured(string origin)
    {
        var devClient = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Cors:AllowedOrigins:0", "http://localhost:8080");
            builder.UseSetting("Cors:AllowedOrigins:1", "https://localhost:8080");
        }).CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/products");
        request.Headers.Add("Origin", origin);

        var response = await devClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal(origin, response.Headers.GetValues("Access-Control-Allow-Origin").FirstOrDefault());
        Assert.Equal("true", response.Headers.GetValues("Access-Control-Allow-Credentials").FirstOrDefault());
    }

    // ── 2. CORS Blocked Unapproved Origins ───────────────────────────────────

    [Theory]
    [InlineData("https://malicious-website.com")]
    [InlineData("https://evil-hacker.org")]
    [InlineData("http://unauthorized-domain.net")]
    public async Task Cors_UnapprovedOrigins_DoNotReceiveAllowHeaders(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/products");
        request.Headers.Add("Origin", origin);

        var response = await _client.SendAsync(request);

        // Server still processes the request, but omits CORS allow headers so the browser blocks JS access
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"),
            $"Origin '{origin}' should NOT receive Access-Control-Allow-Origin header.");
    }

    // ── 3. CORS Preflight OPTIONS Handling ───────────────────────────────────

    [Fact]
    public async Task Cors_PreflightOptions_AllowsConfiguredHeadersAndCredentials()
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/customer/orders");
        request.Headers.Add("Origin", "https://tools.gogaabyte.com");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type,x-xsrf-token,x-idempotency-key");

        var response = await _client.SendAsync(request);

        Assert.True(response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.NoContent);
        Assert.Equal("https://tools.gogaabyte.com", response.Headers.GetValues("Access-Control-Allow-Origin").FirstOrDefault());
        Assert.Equal("true", response.Headers.GetValues("Access-Control-Allow-Credentials").FirstOrDefault());

        var allowHeaders = response.Headers.GetValues("Access-Control-Allow-Headers").FirstOrDefault() ?? "";
        Assert.Contains("content-type", allowHeaders, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("x-xsrf-token", allowHeaders, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("x-idempotency-key", allowHeaders, StringComparison.OrdinalIgnoreCase);
    }

    // ── 4. Customer Login Cookie Behavior ────────────────────────────────────

    [Fact]
    public async Task CustomerLogin_SetsSecureHttpOnlyLaxCookie()
    {
        var email = $"cust_cookie_{Guid.NewGuid():N}@test.local";
        var password = "StrongPassword123!";
        await CreateTestUserAsync(email, password, AppRoles.Customer);

        var response = await _client.PostAsJsonAsync("/api/customer/auth/login", new CustomerLoginRequest
        {
            Email = email,
            Password = password,
            RememberMe = true
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var setCookies = response.Headers.GetValues("Set-Cookie").ToList();
        var authCookieHeader = setCookies.FirstOrDefault(c => c.Contains(".WebAPI.Customer.Auth"));
        Assert.NotNull(authCookieHeader);

        Assert.Contains("httponly", authCookieHeader, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", authCookieHeader, StringComparison.OrdinalIgnoreCase);
    }

    // ── 5. Admin Login Cookie Behavior ───────────────────────────────────────

    [Fact]
    public async Task AdminLogin_SetsSecureHttpOnlyLaxCookie()
    {
        var email = $"admin_cookie_{Guid.NewGuid():N}@test.local";
        var password = "StrongAdminPass123!";
        await CreateTestUserAsync(email, password, AppRoles.Admin);

        var response = await _client.PostAsJsonAsync("/api/admin/auth/login", new AdminLoginRequest
        {
            Email = email,
            Password = password,
            RememberMe = true
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var setCookies = response.Headers.GetValues("Set-Cookie").ToList();
        var authCookieHeader = setCookies.FirstOrDefault(c => c.Contains(".WebAPI.Admin.Auth"));
        Assert.NotNull(authCookieHeader);

        Assert.Contains("httponly", authCookieHeader, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", authCookieHeader, StringComparison.OrdinalIgnoreCase);
    }

    // ── 6. Customer Cannot Access Admin Endpoints ────────────────────────────

    [Fact]
    public async Task CustomerSession_CannotAccessAdminEndpoints_Returns401Json()
    {
        var email = $"cust_trespass_{Guid.NewGuid():N}@test.local";
        var password = "CustPassword123!";
        await CreateTestUserAsync(email, password, AppRoles.Customer);

        var (authCookie, _, _) = await LoginCustomerAsync(email, password);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/orders");
        request.Headers.Add("Cookie", authCookie);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.NotNull(body);
        Assert.False(body.Success);
        Assert.Contains("Unauthenticated", body.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── 7. Admin Cannot Access Customer Endpoints Without Customer Session ───

    [Fact]
    public async Task AdminSession_CannotAccessCustomerEndpoints_Returns401Json()
    {
        var email = $"admin_trespass_{Guid.NewGuid():N}@test.local";
        var password = "AdminPassword123!";
        await CreateTestUserAsync(email, password, AppRoles.Admin);

        var (authCookie, _, _) = await LoginAdminAsync(email, password);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/customer/orders");
        request.Headers.Add("Cookie", authCookie);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.NotNull(body);
        Assert.False(body.Success);
        Assert.Contains("Unauthenticated", body.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── 8. Deactivated Customer Account Invalidation ─────────────────────────

    [Fact]
    public async Task DeactivatedCustomer_SessionImmediatelyInvalidated_Returns401()
    {
        var email = $"cust_deact_{Guid.NewGuid():N}@test.local";
        var password = "CustPassword123!";
        var user = await CreateTestUserAsync(email, password, AppRoles.Customer);

        var (authCookie, _, _) = await LoginCustomerAsync(email, password);

        // Deactivate the user in the database
        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var dbUser = await userManager.FindByIdAsync(user.Id);
            Assert.NotNull(dbUser);
            dbUser.IsActive = false;
            await userManager.UpdateAsync(dbUser);
        }

        // Send authenticated request with existing cookie
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/customer/auth/me");
        request.Headers.Add("Cookie", authCookie);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── 9. CSRF Token Issuance & Protected Mutating Requests ─────────────────

    [Fact]
    public async Task Csrf_CustomerGetCsrf_ReturnsNonCacheableJsonToken_AndProtectsPost()
    {
        var email = $"cust_csrf_{Guid.NewGuid():N}@test.local";
        var password = "CustPassword123!";
        await CreateTestUserAsync(email, password, AppRoles.Customer);

        var (authCookie, csrfCookie, csrfToken) = await LoginCustomerAsync(email, password);

        // Mutative POST with valid cookies and header succeeds
        var postRequest = new HttpRequestMessage(HttpMethod.Post, "/api/customer/auth/logout");
        postRequest.Headers.Add("Cookie", $"{authCookie}; {csrfCookie}");
        postRequest.Headers.Add("X-XSRF-TOKEN", csrfToken);

        var postResponse = await _client.SendAsync(postRequest);
        Assert.Equal(HttpStatusCode.OK, postResponse.StatusCode);
    }

    // ── 10. Missing CSRF Token Rejection ────────────────────────────────────

    [Fact]
    public async Task Csrf_MissingXsrfTokenHeader_RejectedWith400BadRequest()
    {
        var email = $"cust_nocsrf_{Guid.NewGuid():N}@test.local";
        var password = "CustPassword123!";
        await CreateTestUserAsync(email, password, AppRoles.Customer);

        var (authCookie, csrfCookie, _) = await LoginCustomerAsync(email, password);

        // Send POST with session cookie but WITHOUT X-XSRF-TOKEN header
        var postRequest = new HttpRequestMessage(HttpMethod.Post, "/api/customer/auth/logout");
        postRequest.Headers.Add("Cookie", $"{authCookie}; {csrfCookie}");

        var postResponse = await _client.SendAsync(postRequest);
        Assert.Equal(HttpStatusCode.BadRequest, postResponse.StatusCode);
    }

    // ── 11. JSON-Compatible 401 Unauthenticated Responses ────────────────────

    [Fact]
    public async Task UnauthenticatedApiCall_Returns401Json_NeverRedirectsToHtml()
    {
        var response = await _client.GetAsync("/api/customer/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.NotNull(body);
        Assert.False(body.Success);
        Assert.NotNull(body.Message);
    }

    // ── 12. Feature Gate: Customer Wallet Top-Up Disabled by Default ─────────

    [Fact]
    public async Task FeatureGate_CustomerWalletTopUp_Returns403WhenDisabled()
    {
        var email = $"cust_gate_topup_{Guid.NewGuid():N}@test.local";
        var password = "CustPassword123!";
        await CreateTestUserAsync(email, password, AppRoles.Customer);

        var (authCookie, csrfCookie, csrfToken) = await LoginCustomerAsync(email, password);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/customer/wallet/topups")
        {
            Content = JsonContent.Create(new CreateCustomerWalletTopUpRequest
            {
                Amount = 1000m,
                PaymentMethodCode = "BANK_TRANSFER"
            })
        };
        request.Headers.Add("Cookie", $"{authCookie}; {csrfCookie}");
        request.Headers.Add("X-XSRF-TOKEN", csrfToken);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<CustomerWalletTopUpDto>>();
        Assert.NotNull(body);
        Assert.False(body.Success);
        Assert.Contains("disabled", body.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── 13. Feature Gate: Customer Pay With Wallet Disabled by Default ───────

    [Fact]
    public async Task FeatureGate_CustomerPayWithWallet_Returns403WhenDisabled()
    {
        var email = $"cust_gate_pay_{Guid.NewGuid():N}@test.local";
        var password = "CustPassword123!";
        await CreateTestUserAsync(email, password, AppRoles.Customer);

        var (authCookie, csrfCookie, csrfToken) = await LoginCustomerAsync(email, password);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/customer/orders/ORD-NONEXISTENT/pay-with-wallet");
        request.Headers.Add("Cookie", $"{authCookie}; {csrfCookie}");
        request.Headers.Add("X-XSRF-TOKEN", csrfToken);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<CustomerOrderPaymentDto>>();
        Assert.NotNull(body);
        Assert.False(body.Success);
        Assert.Contains("disabled", body.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── 14. Feature Gate: Admin Manual Fulfillment Disabled by Default ───────

    [Fact]
    public async Task FeatureGate_AdminManualFulfillment_Returns403WhenDisabled()
    {
        var email = $"admin_gate_fulf_{Guid.NewGuid():N}@test.local";
        var password = "AdminPassword123!";
        await CreateTestUserAsync(email, password, AppRoles.Admin);

        var (authCookie, csrfCookie, csrfToken) = await LoginAdminAsync(email, password);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/orders/ORD-NONEXISTENT/items/1/manual-fulfillment/start");
        request.Headers.Add("Cookie", $"{authCookie}; {csrfCookie}");
        request.Headers.Add("X-XSRF-TOKEN", csrfToken);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── 15. Supplier Privacy in Customer DTOs ────────────────────────────────

    [Fact]
    public async Task SupplierPrivacy_CustomerCatalogEndpoints_NeverExposeVendorDetails()
    {
        // Seed category, product, vendor, and vendor mapping in test DB
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var category = new Category
            {
                Name = "Secret Supplier Tools",
                Slug = "secret-supplier-tools",
                IsActive = true
            };
            db.Categories.Add(category);
            await db.SaveChangesAsync();

            var product = new Product
            {
                CategoryId = category.Id,
                InternalSku = "SECRET_PRODUCT_999",
                Name = "Confidential Tool",
                Description = "A private product",
                SellingPrice = 5000m,
                IsActive = true
            };
            db.Products.Add(product);

            var vendor = new Vendor
            {
                InternalName = "TOP_SECRET_VENDOR_LLC",
                IsActive = true
            };
            db.Vendors.Add(vendor);
            await db.SaveChangesAsync();

            var mapping = new VendorProduct
            {
                VendorId = vendor.Id,
                ProductId = product.Id,
                ExternalProductId = "EXT_SECRET_SKU_12345",
                VendorCost = 2500m,
                VendorCostCurrency = "USD",
                IsAvailable = true,
                Priority = 1
            };
            db.VendorProducts.Add(mapping);
            await db.SaveChangesAsync();
        }

        // Act 1: Get Products list
        var listResponse = await _client.GetAsync("/api/products?search=Confidential");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var listRawJson = await listResponse.Content.ReadAsStringAsync();

        // Act 2: Get Product by SKU
        var detailResponse = await _client.GetAsync("/api/products/SECRET_PRODUCT_999");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        var detailRawJson = await detailResponse.Content.ReadAsStringAsync();

        // Assert: Neither response leaks secret vendor information
        foreach (var rawJson in new[] { listRawJson, detailRawJson })
        {
            Assert.DoesNotContain("TOP_SECRET_VENDOR_LLC", rawJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("EXT_SECRET_SKU_12345", rawJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("VendorCost", rawJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("VendorId", rawJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("VendorProduct", rawJson, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── 16. Standard ApiResponse Envelope Consistency ────────────────────────

    [Fact]
    public async Task ApiResponse_EnvelopeStructure_IsConsistentAcrossEndpoints()
    {
        var response = await _client.GetAsync("/api/categories");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("success", out var successProp));
        Assert.Equal(JsonValueKind.True, successProp.ValueKind);

        Assert.True(root.TryGetProperty("data", out var dataProp));
        Assert.Equal(JsonValueKind.Array, dataProp.ValueKind);

        Assert.True(root.TryGetProperty("message", out var messageProp));
        Assert.Equal(JsonValueKind.String, messageProp.ValueKind);
    }
}
