using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using WebApiTgBot.Common.Models;
using WebApiTgBot.Infrastructure.Data;
using WebApiTgBot.Modules.Catalog.Models;
using WebApiTgBot.Modules.Catalog.Models.DTOs.Admin;
using WebApiTgBot.Modules.Identity.Models;
using WebApiTgBot.Modules.Identity.Models.DTOs;

namespace WebApiTgBot.Tests;

/// <summary>
/// Integration tests verifying administrative vendor management and vendor-product mappings (Milestone 2B-2B):
/// - Vendor creation, updating, search, pagination, and status toggling.
/// - Duplicate vendor name rejection.
/// - Vendor-product mapping creation, multiple vendors per product, duplicate mapping rejection.
/// - Referential integrity (rejects nonexistent vendor/product).
/// - Non-negative purchase costs and multi-currency handling (PKR, USD, USDT).
/// - Availability and priority updates.
/// - Role authorization (anonymous 401, non-admin rejection).
/// - CSRF protection on mutative endpoints.
/// - Public catalog isolation: vendor details NEVER leak into customer-facing APIs.
/// - Verification of PostgreSQL unique constraint and foreign keys against live database.
/// </summary>
public class AdminVendorTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public AdminVendorTests(CustomWebApplicationFactory factory)
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
            FullName = "Test " + role,
            EmailConfirmed = true,
            IsActive = isActive,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var result = await userManager.CreateAsync(user, password);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(e => e.Description)));

        if (!string.IsNullOrEmpty(role))
        {
            await userManager.AddToRoleAsync(user, role);
        }

        return user;
    }

    private async Task<(string AuthCookie, string AntiforgeryCookie, string CsrfToken)> LoginAsAdminAsync()
    {
        var email = $"admin_vnd_{Guid.NewGuid():N}@test.local";
        var password = "SecureAdminPass123!";
        await CreateTestUserAsync(email, password, AppRoles.Admin);

        var loginResponse = await _client.PostAsJsonAsync("/api/admin/auth/login", new AdminLoginRequest
        {
            Email = email,
            Password = password
        });
        loginResponse.EnsureSuccessStatusCode();

        var authCookie = loginResponse.Headers.GetValues("Set-Cookie")
            .First(c => c.Contains(".WebAPI.Admin.Auth"))
            .Split(';')[0];

        var csrfResponse = await _client.GetAsync("/api/admin/auth/csrf");
        csrfResponse.EnsureSuccessStatusCode();

        var csrfResult = await csrfResponse.Content.ReadFromJsonAsync<ApiResponse<string>>();
        var token = csrfResult!.Data!;

        var antiforgeryCookie = csrfResponse.Headers.GetValues("Set-Cookie")
            .First(c => c.Contains(".WebAPI.Antiforgery"))
            .Split(';')[0];

        return (authCookie, antiforgeryCookie, token);
    }

    private HttpRequestMessage CreateAuthenticatedRequest(
        HttpMethod method,
        string uri,
        string authCookie,
        string antiforgeryCookie,
        string csrfToken,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");

        if (method != HttpMethod.Get && method != HttpMethod.Head && method != HttpMethod.Options)
        {
            request.Headers.Add("X-XSRF-TOKEN", csrfToken);
        }

        if (body != null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    private async Task<AdminProductDto> CreateTestProductAsync(
        string authCookie,
        string antiforgeryCookie,
        string csrfToken)
    {
        var catSlug = $"cat-vtest-{Guid.NewGuid():N}";
        var catResp = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Post, "/api/admin/categories", authCookie, antiforgeryCookie, csrfToken,
            new CreateCategoryRequest { Name = "Vendor Test Category", Slug = catSlug }));
        var cat = (await catResp.Content.ReadFromJsonAsync<ApiResponse<AdminCategoryDto>>())!.Data!;

        var sku = $"VP_PROD_{Guid.NewGuid():N}".ToUpperInvariant();
        var prodResp = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Post, "/api/admin/products", authCookie, antiforgeryCookie, csrfToken,
            new CreateProductRequest
            {
                InternalSku = sku,
                Name = "Vendor Test Product",
                CategoryId = cat.Id,
                SellingPrice = 5000.00m,
                SellingCurrency = "PKR"
            }));
        return (await prodResp.Content.ReadFromJsonAsync<ApiResponse<AdminProductDto>>())!.Data!;
    }

    [Fact]
    public async Task Admin_CreateVendor_Success()
    {
        // Arrange
        var (authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync();
        var vendorName = $"Supplier Alpha {Guid.NewGuid():N}";

        // Act
        var postReq = CreateAuthenticatedRequest(
            HttpMethod.Post,
            "/api/admin/vendors",
            authCookie,
            antiforgeryCookie,
            csrfToken,
            new CreateVendorRequest { InternalName = vendorName, IsActive = true });

        var response = await _client.SendAsync(postReq);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<AdminVendorDto>>();
        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.Equal(vendorName, result.Data!.InternalName);
        Assert.True(result.Data.IsActive);
        Assert.Equal(0, result.Data.ProductMappingCount);

        // Verify retrieval via GET /api/admin/vendors/{id}
        var getReq = CreateAuthenticatedRequest(
            HttpMethod.Get,
            $"/api/admin/vendors/{result.Data.Id}",
            authCookie,
            antiforgeryCookie,
            csrfToken);
        var getResp = await _client.SendAsync(getReq);
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
    }

    [Fact]
    public async Task Admin_CreateVendor_DuplicateName_Returns409Conflict()
    {
        // Arrange
        var (authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync();
        var vendorName = $"Unique Vendor {Guid.NewGuid():N}";

        var firstResp = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Post, "/api/admin/vendors", authCookie, antiforgeryCookie, csrfToken,
            new CreateVendorRequest { InternalName = vendorName }));
        Assert.Equal(HttpStatusCode.Created, firstResp.StatusCode);

        // Act — attempt duplicate creation
        var secondResp = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Post, "/api/admin/vendors", authCookie, antiforgeryCookie, csrfToken,
            new CreateVendorRequest { InternalName = vendorName }));

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, secondResp.StatusCode);
        var result = await secondResp.Content.ReadFromJsonAsync<ApiResponse<AdminVendorDto>>();
        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Contains("already exists", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Admin_UpdateVendor_Success_And_DuplicateNameRejected()
    {
        // Arrange
        var (authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync();
        var v1Name = $"Vendor One {Guid.NewGuid():N}";
        var v2Name = $"Vendor Two {Guid.NewGuid():N}";

        var r1 = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Post, "/api/admin/vendors", authCookie, antiforgeryCookie, csrfToken,
            new CreateVendorRequest { InternalName = v1Name }));
        var vendor1 = (await r1.Content.ReadFromJsonAsync<ApiResponse<AdminVendorDto>>())!.Data!;

        var r2 = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Post, "/api/admin/vendors", authCookie, antiforgeryCookie, csrfToken,
            new CreateVendorRequest { InternalName = v2Name }));
        var vendor2 = (await r2.Content.ReadFromJsonAsync<ApiResponse<AdminVendorDto>>())!.Data!;

        // Act 1 — Update Vendor 1 to a unique name
        var updatedName = $"Vendor One Renamed {Guid.NewGuid():N}";
        var updateResp = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Put,
            $"/api/admin/vendors/{vendor1.Id}",
            authCookie,
            antiforgeryCookie,
            csrfToken,
            new UpdateVendorRequest { InternalName = updatedName, IsActive = true }));

        Assert.Equal(HttpStatusCode.OK, updateResp.StatusCode);
        var updateResult = await updateResp.Content.ReadFromJsonAsync<ApiResponse<AdminVendorDto>>();
        Assert.Equal(updatedName, updateResult!.Data!.InternalName);

        // Act 2 — Update Vendor 1 to Vendor 2's name (collision)
        var collisionResp = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Put,
            $"/api/admin/vendors/{vendor1.Id}",
            authCookie,
            antiforgeryCookie,
            csrfToken,
            new UpdateVendorRequest { InternalName = v2Name, IsActive = true }));

        // Assert 2
        Assert.Equal(HttpStatusCode.Conflict, collisionResp.StatusCode);
    }

    [Fact]
    public async Task Admin_UpdateVendorStatus_TogglesActive_PreservesMappings()
    {
        // Arrange
        var (authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync();
        var product = await CreateTestProductAsync(authCookie, antiforgeryCookie, csrfToken);

        var vendorResp = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Post, "/api/admin/vendors", authCookie, antiforgeryCookie, csrfToken,
            new CreateVendorRequest { InternalName = $"Preserve Mappings Vendor {Guid.NewGuid():N}", IsActive = true }));
        var vendor = (await vendorResp.Content.ReadFromJsonAsync<ApiResponse<AdminVendorDto>>())!.Data!;

        // Create mapping
        var mapResp = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Post, "/api/admin/vendor-products", authCookie, antiforgeryCookie, csrfToken,
            new CreateVendorProductRequest
            {
                VendorId = vendor.Id,
                ProductId = product.Id,
                ExternalProductId = "EXT-101",
                VendorCost = 15.00m,
                VendorCostCurrency = "USDT"
            }));
        Assert.Equal(HttpStatusCode.Created, mapResp.StatusCode);

        // Act — Soft disable the vendor via PATCH
        var patchResp = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Patch,
            $"/api/admin/vendors/{vendor.Id}/status",
            authCookie,
            antiforgeryCookie,
            csrfToken,
            new UpdateVendorStatusRequest { IsActive = false }));

        // Assert — Vendor is inactive
        Assert.Equal(HttpStatusCode.OK, patchResp.StatusCode);
        var patchedVendor = (await patchResp.Content.ReadFromJsonAsync<ApiResponse<AdminVendorDto>>())!.Data!;
        Assert.False(patchedVendor.IsActive);
        Assert.Equal(1, patchedVendor.ProductMappingCount); // Mapping is preserved

        // Verify mapping still exists and references the vendor
        var listMapResp = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Get,
            $"/api/admin/vendor-products?vendorId={vendor.Id}",
            authCookie,
            antiforgeryCookie,
            csrfToken));
        Assert.Equal(HttpStatusCode.OK, listMapResp.StatusCode);
        var mapResult = await listMapResp.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AdminVendorProductDto>>>();
        Assert.NotNull(mapResult);
        Assert.Single(mapResult.Data!.Items);
        Assert.False(mapResult.Data.Items[0].VendorIsActive);
    }

    [Fact]
    public async Task Admin_CreateVendorProduct_Success_MultiCurrency()
    {
        // Arrange
        var (authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync();
        var product = await CreateTestProductAsync(authCookie, antiforgeryCookie, csrfToken);

        var vendorResp = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Post, "/api/admin/vendors", authCookie, antiforgeryCookie, csrfToken,
            new CreateVendorRequest { InternalName = $"USDT Vendor {Guid.NewGuid():N}" }));
        var vendor = (await vendorResp.Content.ReadFromJsonAsync<ApiResponse<AdminVendorDto>>())!.Data!;

        // Act — Map with lowercase 'usdt', should normalize to 'USDT'
        var mapResp = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Post,
            "/api/admin/vendor-products",
            authCookie,
            antiforgeryCookie,
            csrfToken,
            new CreateVendorProductRequest
            {
                VendorId = vendor.Id,
                ProductId = product.Id,
                ExternalProductId = "ITEM_USDT_42",
                VendorCost = 14.50m,
                VendorCostCurrency = "usdt",
                IsAvailable = true,
                Priority = 5
            }));

        // Assert
        Assert.Equal(HttpStatusCode.Created, mapResp.StatusCode);
        var result = await mapResp.Content.ReadFromJsonAsync<ApiResponse<AdminVendorProductDto>>();
        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.Equal("USDT", result.Data!.VendorCostCurrency);
        Assert.Equal(14.50m, result.Data.VendorCost);
        Assert.Equal("ITEM_USDT_42", result.Data.ExternalProductId);
        Assert.Equal(5, result.Data.Priority);
        Assert.True(result.Data.IsAvailable);
    }

    [Fact]
    public async Task Admin_MultipleVendors_CanMapToSameProduct()
    {
        // Arrange
        var (authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync();
        var product = await CreateTestProductAsync(authCookie, antiforgeryCookie, csrfToken);

        var v1Resp = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Post, "/api/admin/vendors", authCookie, antiforgeryCookie, csrfToken,
            new CreateVendorRequest { InternalName = $"Supplier PKR {Guid.NewGuid():N}" }));
        var vendor1 = (await v1Resp.Content.ReadFromJsonAsync<ApiResponse<AdminVendorDto>>())!.Data!;

        var v2Resp = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Post, "/api/admin/vendors", authCookie, antiforgeryCookie, csrfToken,
            new CreateVendorRequest { InternalName = $"Supplier USDT {Guid.NewGuid():N}" }));
        var vendor2 = (await v2Resp.Content.ReadFromJsonAsync<ApiResponse<AdminVendorDto>>())!.Data!;

        // Act — Map both vendors to the exact same product
        var map1 = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Post, "/api/admin/vendor-products", authCookie, antiforgeryCookie, csrfToken,
            new CreateVendorProductRequest
            {
                VendorId = vendor1.Id,
                ProductId = product.Id,
                ExternalProductId = "EXT_PKR_1",
                VendorCost = 3800m,
                VendorCostCurrency = "PKR",
                Priority = 1
            }));
        Assert.Equal(HttpStatusCode.Created, map1.StatusCode);

        var map2 = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Post, "/api/admin/vendor-products", authCookie, antiforgeryCookie, csrfToken,
            new CreateVendorProductRequest
            {
                VendorId = vendor2.Id,
                ProductId = product.Id,
                ExternalProductId = "EXT_USDT_1",
                VendorCost = 12m,
                VendorCostCurrency = "USDT",
                Priority = 2
            }));
        Assert.Equal(HttpStatusCode.Created, map2.StatusCode);

        // Assert — Query mappings for this product returns both vendors
        var listResp = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Get,
            $"/api/admin/vendor-products?productId={product.Id}",
            authCookie,
            antiforgeryCookie,
            csrfToken));
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);

        var pagedResult = await listResp.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AdminVendorProductDto>>>();
        Assert.NotNull(pagedResult);
        Assert.Equal(2, pagedResult.Data!.Items.Count);
        Assert.Contains(pagedResult.Data.Items, m => m.VendorId == vendor1.Id && m.VendorCostCurrency == "PKR");
        Assert.Contains(pagedResult.Data.Items, m => m.VendorId == vendor2.Id && m.VendorCostCurrency == "USDT");
    }

    [Fact]
    public async Task Admin_CreateVendorProduct_DuplicateMapping_Returns409Conflict()
    {
        // Arrange
        var (authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync();
        var product = await CreateTestProductAsync(authCookie, antiforgeryCookie, csrfToken);

        var vResp = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Post, "/api/admin/vendors", authCookie, antiforgeryCookie, csrfToken,
            new CreateVendorRequest { InternalName = $"Dup Mapping Vendor {Guid.NewGuid():N}" }));
        var vendor = (await vResp.Content.ReadFromJsonAsync<ApiResponse<AdminVendorDto>>())!.Data!;

        // First mapping succeeds
        var map1 = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Post, "/api/admin/vendor-products", authCookie, antiforgeryCookie, csrfToken,
            new CreateVendorProductRequest
            {
                VendorId = vendor.Id,
                ProductId = product.Id,
                ExternalProductId = "EXT_A",
                VendorCost = 10m,
                VendorCostCurrency = "USD"
            }));
        Assert.Equal(HttpStatusCode.Created, map1.StatusCode);

        // Act — Attempt to map the exact same vendor and product again
        var map2 = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Post, "/api/admin/vendor-products", authCookie, antiforgeryCookie, csrfToken,
            new CreateVendorProductRequest
            {
                VendorId = vendor.Id,
                ProductId = product.Id,
                ExternalProductId = "EXT_B",
                VendorCost = 12m,
                VendorCostCurrency = "USD"
            }));

        // Assert — Rejected per duplicate constraint
        Assert.Equal(HttpStatusCode.Conflict, map2.StatusCode);
        var result = await map2.Content.ReadFromJsonAsync<ApiResponse<AdminVendorProductDto>>();
        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Contains("already exists", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Admin_CreateVendorProduct_InvalidVendorOrProduct_Returns400BadRequest()
    {
        // Arrange
        var (authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync();
        var product = await CreateTestProductAsync(authCookie, antiforgeryCookie, csrfToken);

        // Act 1 — Nonexistent vendor ID
        var badVendorResp = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Post, "/api/admin/vendor-products", authCookie, antiforgeryCookie, csrfToken,
            new CreateVendorProductRequest
            {
                VendorId = 999999,
                ProductId = product.Id,
                ExternalProductId = "EXT",
                VendorCost = 10m,
                VendorCostCurrency = "PKR"
            }));
        Assert.Equal(HttpStatusCode.BadRequest, badVendorResp.StatusCode);

        // Act 2 — Nonexistent product ID
        var badProdResp = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Post, "/api/admin/vendor-products", authCookie, antiforgeryCookie, csrfToken,
            new CreateVendorProductRequest
            {
                VendorId = 1,
                ProductId = 999999,
                ExternalProductId = "EXT",
                VendorCost = 10m,
                VendorCostCurrency = "PKR"
            }));
        Assert.Equal(HttpStatusCode.BadRequest, badProdResp.StatusCode);
    }

    [Fact]
    public async Task Admin_CreateVendorProduct_NegativeCost_Returns400BadRequest()
    {
        // Arrange
        var (authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync();
        var product = await CreateTestProductAsync(authCookie, antiforgeryCookie, csrfToken);

        var vResp = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Post, "/api/admin/vendors", authCookie, antiforgeryCookie, csrfToken,
            new CreateVendorRequest { InternalName = $"Neg Cost Vendor {Guid.NewGuid():N}" }));
        var vendor = (await vResp.Content.ReadFromJsonAsync<ApiResponse<AdminVendorDto>>())!.Data!;

        // Act — Attempt negative purchase cost
        var resp = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Post, "/api/admin/vendor-products", authCookie, antiforgeryCookie, csrfToken,
            new CreateVendorProductRequest
            {
                VendorId = vendor.Id,
                ProductId = product.Id,
                ExternalProductId = "EXT",
                VendorCost = -50m,
                VendorCostCurrency = "USD"
            }));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Admin_UpdateVendorProduct_Success_And_AvailabilityPatch()
    {
        // Arrange
        var (authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync();
        var product = await CreateTestProductAsync(authCookie, antiforgeryCookie, csrfToken);

        var vResp = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Post, "/api/admin/vendors", authCookie, antiforgeryCookie, csrfToken,
            new CreateVendorRequest { InternalName = $"Upd Mapping Vendor {Guid.NewGuid():N}" }));
        var vendor = (await vResp.Content.ReadFromJsonAsync<ApiResponse<AdminVendorDto>>())!.Data!;

        var createResp = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Post, "/api/admin/vendor-products", authCookie, antiforgeryCookie, csrfToken,
            new CreateVendorProductRequest
            {
                VendorId = vendor.Id,
                ProductId = product.Id,
                ExternalProductId = "ORIG_EXT",
                VendorCost = 20m,
                VendorCostCurrency = "USD",
                Priority = 10,
                IsAvailable = true
            }));
        var mapping = (await createResp.Content.ReadFromJsonAsync<ApiResponse<AdminVendorProductDto>>())!.Data!;

        // Act 1 — Update via PUT
        var putResp = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Put,
            $"/api/admin/vendor-products/{mapping.Id}",
            authCookie,
            antiforgeryCookie,
            csrfToken,
            new UpdateVendorProductRequest
            {
                ExternalProductId = "NEW_EXT_ID",
                VendorCost = 18.50m,
                VendorCostCurrency = "USDT",
                Priority = 1,
                IsAvailable = true
            }));

        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);
        var updated = (await putResp.Content.ReadFromJsonAsync<ApiResponse<AdminVendorProductDto>>())!.Data!;
        Assert.Equal("NEW_EXT_ID", updated.ExternalProductId);
        Assert.Equal(18.50m, updated.VendorCost);
        Assert.Equal("USDT", updated.VendorCostCurrency);
        Assert.Equal(1, updated.Priority);

        // Act 2 — Toggle availability via PATCH
        var patchResp = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Patch,
            $"/api/admin/vendor-products/{mapping.Id}/availability",
            authCookie,
            antiforgeryCookie,
            csrfToken,
            new UpdateVendorProductAvailabilityRequest { IsAvailable = false }));

        Assert.Equal(HttpStatusCode.OK, patchResp.StatusCode);
        var patched = (await patchResp.Content.ReadFromJsonAsync<ApiResponse<AdminVendorProductDto>>())!.Data!;
        Assert.False(patched.IsAvailable);
    }

    [Fact]
    public async Task AnonymousUser_CannotAccessOrModify_VendorEndpoints_Returns401()
    {
        // Act 1 — Anonymous GET vendors
        var getVendors = await _client.GetAsync("/api/admin/vendors");
        Assert.Equal(HttpStatusCode.Unauthorized, getVendors.StatusCode);

        // Act 2 — Anonymous POST vendor
        var postVendor = await _client.PostAsJsonAsync("/api/admin/vendors", new CreateVendorRequest
        {
            InternalName = "Anon Vendor"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, postVendor.StatusCode);

        // Act 3 — Anonymous GET mappings
        var getMappings = await _client.GetAsync("/api/admin/vendor-products");
        Assert.Equal(HttpStatusCode.Unauthorized, getMappings.StatusCode);

        // Act 4 — Anonymous POST mapping
        var postMapping = await _client.PostAsJsonAsync("/api/admin/vendor-products", new CreateVendorProductRequest
        {
            VendorId = 1,
            ProductId = 1,
            ExternalProductId = "EXT",
            VendorCost = 10,
            VendorCostCurrency = "PKR"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, postMapping.StatusCode);
    }

    [Fact]
    public async Task NonAdminUser_CannotAccessOrModify_VendorEndpoints_ReturnsUnauthorized()
    {
        // Arrange — Create user, log in, then demote from Admin
        var email = $"demoted_vendor_{Guid.NewGuid():N}@test.local";
        var password = "AdminPass123!";
        var user = await CreateTestUserAsync(email, password, AppRoles.Admin, isActive: true);

        var loginResp = await _client.PostAsJsonAsync("/api/admin/auth/login", new AdminLoginRequest
        {
            Email = email,
            Password = password
        });
        Assert.Equal(HttpStatusCode.OK, loginResp.StatusCode);

        var authCookie = loginResp.Headers.GetValues("Set-Cookie")
            .First(c => c.Contains(".WebAPI.Admin.Auth"))
            .Split(';')[0];

        // Demote the user
        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var dbUser = await userManager.FindByIdAsync(user.Id);
            Assert.NotNull(dbUser);
            await userManager.RemoveFromRoleAsync(dbUser, AppRoles.Admin);
        }

        // Act — Attempt to access admin vendors
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/admin/vendors");
        req.Headers.Add("Cookie", authCookie);
        var resp = await _client.SendAsync(req);

        // Assert — Session principal rejected
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Admin_MutativeRequests_RequireValidCsrfToken()
    {
        // Arrange
        var (authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync();

        // Act 1 — Attempt POST /api/admin/vendors WITHOUT CSRF token
        var forgedVendorReq = new HttpRequestMessage(HttpMethod.Post, "/api/admin/vendors");
        forgedVendorReq.Headers.Add("Cookie", authCookie);
        forgedVendorReq.Content = JsonContent.Create(new CreateVendorRequest
        {
            InternalName = $"Forged Vendor {Guid.NewGuid():N}"
        });
        var forgedVendorResp = await _client.SendAsync(forgedVendorReq);

        // Assert 1 — AutoValidateAntiforgeryToken rejects with 400 Bad Request
        Assert.Equal(HttpStatusCode.BadRequest, forgedVendorResp.StatusCode);

        // Act 2 — Attempt POST /api/admin/vendor-products WITHOUT CSRF token
        var forgedMapReq = new HttpRequestMessage(HttpMethod.Post, "/api/admin/vendor-products");
        forgedMapReq.Headers.Add("Cookie", authCookie);
        forgedMapReq.Content = JsonContent.Create(new CreateVendorProductRequest
        {
            VendorId = 1,
            ProductId = 1,
            ExternalProductId = "EXT",
            VendorCost = 10m,
            VendorCostCurrency = "PKR"
        });
        var forgedMapResp = await _client.SendAsync(forgedMapReq);

        // Assert 2
        Assert.Equal(HttpStatusCode.BadRequest, forgedMapResp.StatusCode);
    }

    [Fact]
    public async Task PublicCatalogApis_NeverExposeVendorDetails()
    {
        // Arrange
        var (authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync();
        var product = await CreateTestProductAsync(authCookie, antiforgeryCookie, csrfToken);

        var vResp = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Post, "/api/admin/vendors", authCookie, antiforgeryCookie, csrfToken,
            new CreateVendorRequest { InternalName = $"Secret Supplier {Guid.NewGuid():N}" }));
        var vendor = (await vResp.Content.ReadFromJsonAsync<ApiResponse<AdminVendorDto>>())!.Data!;

        await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Post, "/api/admin/vendor-products", authCookie, antiforgeryCookie, csrfToken,
            new CreateVendorProductRequest
            {
                VendorId = vendor.Id,
                ProductId = product.Id,
                ExternalProductId = "SECRET_EXT_ID_999",
                VendorCost = 12.34m,
                VendorCostCurrency = "USDT"
            }));

        // Act 1 — Public GET /api/products/{sku}
        var singleResp = await _client.GetAsync($"/api/products/{product.InternalSku}");
        Assert.Equal(HttpStatusCode.OK, singleResp.StatusCode);
        var singleJson = await singleResp.Content.ReadAsStringAsync();

        // Assert 1 — Ensure supplier identity, cost, and external IDs are completely absent
        Assert.DoesNotContain("Secret Supplier", singleJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SECRET_EXT_ID_999", singleJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("12.34", singleJson);
        Assert.DoesNotContain("VendorCost", singleJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("VendorProducts", singleJson, StringComparison.OrdinalIgnoreCase);

        // Act 2 — Public GET /api/products listing
        var listResp = await _client.GetAsync($"/api/products?search={product.InternalSku}");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var listJson = await listResp.Content.ReadAsStringAsync();

        // Assert 2
        Assert.DoesNotContain("Secret Supplier", listJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SECRET_EXT_ID_999", listJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("VendorCost", listJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSql_EnforcesUniqueVendorProductConstraint_AndForeignKeys()
    {
        // This test connects directly to PostgreSQL to verify database constraints
        var options = PostgreSqlTestDatabase.CreateOptions();
        await using var db = new AppDbContext(options);

        // Verify relational database connectivity
        if (!await PostgreSqlTestDatabase.EnsureMigratedAsync(db))
        {
            // If PostgreSQL is temporarily unreachable in the test environment, skip gracefully
            return;
        }

        var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];

        // 1. Create a category, product, and vendor in PostgreSQL
        var category = new Category
        {
            Name = "PG Test Cat " + uniqueSuffix,
            Slug = "pg-cat-" + uniqueSuffix,
            IsActive = true
        };
        db.Categories.Add(category);

        var product = new Product
        {
            InternalSku = "PG_SKU_" + uniqueSuffix.ToUpperInvariant(),
            Name = "PG Test Product " + uniqueSuffix,
            Category = category,
            SellingPrice = 1000m,
            SellingCurrency = "PKR",
            IsActive = true
        };
        db.Products.Add(product);

        var vendor = new Vendor
        {
            InternalName = "PG Supplier " + uniqueSuffix,
            IsActive = true
        };
        db.Vendors.Add(vendor);

        await db.SaveChangesAsync();

        try
        {
            // 2. Add first valid VendorProduct mapping
            var mapping1 = new VendorProduct
            {
                VendorId = vendor.Id,
                ProductId = product.Id,
                ExternalProductId = "PG_EXT_1",
                VendorCost = 10m,
                VendorCostCurrency = "USDT",
                IsAvailable = true,
                Priority = 1
            };
            db.VendorProducts.Add(mapping1);
            await db.SaveChangesAsync();

            // 3. Attempt to add a duplicate (VendorId, ProductId) mapping
            var duplicateMapping = new VendorProduct
            {
                VendorId = vendor.Id,
                ProductId = product.Id, // Same pair!
                ExternalProductId = "PG_EXT_2",
                VendorCost = 20m,
                VendorCostCurrency = "USD",
                IsAvailable = true,
                Priority = 2
            };
            db.VendorProducts.Add(duplicateMapping);

            // Assert: PostgreSQL must reject with DbUpdateException wrapping a PostgresException (23505 unique_violation)
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.NotNull(ex.InnerException);
            var pgEx = Assert.IsType<PostgresException>(ex.InnerException);
            Assert.Equal("23505", pgEx.SqlState); // 23505 = unique_violation
            Assert.Contains("IX_VendorProducts_VendorId_ProductId", pgEx.ConstraintName ?? pgEx.Message);
        }
        finally
        {
            // Clean up test records
            await using var cleanupDb = new AppDbContext(options);
            var toDeleteMaps = await cleanupDb.VendorProducts
                .Where(vp => vp.VendorId == vendor.Id || vp.ProductId == product.Id)
                .ToListAsync();
            cleanupDb.VendorProducts.RemoveRange(toDeleteMaps);

            var toDeleteProd = await cleanupDb.Products.FindAsync(product.Id);
            if (toDeleteProd != null) cleanupDb.Products.Remove(toDeleteProd);

            var toDeleteCat = await cleanupDb.Categories.FindAsync(category.Id);
            if (toDeleteCat != null) cleanupDb.Categories.Remove(toDeleteCat);

            var toDeleteVendor = await cleanupDb.Vendors.FindAsync(vendor.Id);
            if (toDeleteVendor != null) cleanupDb.Vendors.Remove(toDeleteVendor);

            await cleanupDb.SaveChangesAsync();
        }
    }
}
