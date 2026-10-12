using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using WebApiTgBot.Common.Models;
using WebApiTgBot.Modules.Catalog.Models.DTOs;
using WebApiTgBot.Modules.Catalog.Models.DTOs.Admin;
using WebApiTgBot.Modules.Identity.Models;
using WebApiTgBot.Modules.Identity.Models.DTOs;

namespace WebApiTgBot.Tests;

/// <summary>
/// Integration tests verifying administrative catalog management (Milestone 2B-2A):
/// - Category creation, editing, status toggling, and slug uniqueness.
/// - Product creation, editing, status toggling, SKU uniqueness, and price validation.
/// - Role authorization (anonymous 401, non-admin rejection).
/// - CSRF protection on mutative endpoints (POST, PUT, PATCH).
/// - Public visibility isolation: inactive products and products in disabled categories are hidden from public APIs.
/// </summary>
public class AdminCatalogTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public AdminCatalogTests(CustomWebApplicationFactory factory)
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
        var email = $"admin_cat_{Guid.NewGuid():N}@test.local";
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

    [Fact]
    public async Task Admin_CreateCategory_Success()
    {
        // Arrange
        var (authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync();
        var slug = $"ai-writing-{Guid.NewGuid():N}";
        var request = new CreateCategoryRequest
        {
            Name = "AI Writing Tools",
            Slug = slug,
            Description = "Automated content generation tools",
            IsActive = true
        };

        // Act
        var postRequest = CreateAuthenticatedRequest(
            HttpMethod.Post,
            "/api/admin/categories",
            authCookie,
            antiforgeryCookie,
            csrfToken,
            request);

        var response = await _client.SendAsync(postRequest);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<AdminCategoryDto>>();
        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.Equal("AI Writing Tools", result.Data!.Name);
        Assert.Equal(slug, result.Data.Slug);
        Assert.True(result.Data.IsActive);
        Assert.True(result.Data.Id > 0);

        // Verify retrieval via GET /api/admin/categories/{id}
        var getReq = CreateAuthenticatedRequest(
            HttpMethod.Get,
            $"/api/admin/categories/{result.Data.Id}",
            authCookie,
            antiforgeryCookie,
            csrfToken);

        var getResp = await _client.SendAsync(getReq);
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
    }

    [Fact]
    public async Task Admin_CreateCategory_DuplicateSlug_Returns409Conflict()
    {
        // Arrange
        var (authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync();
        var slug = $"dup-slug-{Guid.NewGuid():N}";

        var firstRequest = new CreateCategoryRequest
        {
            Name = "First Category",
            Slug = slug
        };

        var firstPost = CreateAuthenticatedRequest(
            HttpMethod.Post,
            "/api/admin/categories",
            authCookie,
            antiforgeryCookie,
            csrfToken,
            firstRequest);
        var firstResp = await _client.SendAsync(firstPost);
        Assert.Equal(HttpStatusCode.Created, firstResp.StatusCode);

        // Act — Attempt to create a second category with the identical slug
        var secondRequest = new CreateCategoryRequest
        {
            Name = "Second Category",
            Slug = slug
        };

        var secondPost = CreateAuthenticatedRequest(
            HttpMethod.Post,
            "/api/admin/categories",
            authCookie,
            antiforgeryCookie,
            csrfToken,
            secondRequest);
        var secondResp = await _client.SendAsync(secondPost);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, secondResp.StatusCode);
        var result = await secondResp.Content.ReadFromJsonAsync<ApiResponse<AdminCategoryDto>>();
        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Contains("already exists", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Admin_UpdateCategory_Success_And_DuplicateSlugRejected()
    {
        // Arrange
        var (authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync();
        var slug1 = $"cat1-{Guid.NewGuid():N}";
        var slug2 = $"cat2-{Guid.NewGuid():N}";

        var create1 = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Post, "/api/admin/categories", authCookie, antiforgeryCookie, csrfToken,
            new CreateCategoryRequest { Name = "Cat 1", Slug = slug1 }));
        var cat1 = (await create1.Content.ReadFromJsonAsync<ApiResponse<AdminCategoryDto>>())!.Data!;

        var create2 = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Post, "/api/admin/categories", authCookie, antiforgeryCookie, csrfToken,
            new CreateCategoryRequest { Name = "Cat 2", Slug = slug2 }));
        var cat2 = (await create2.Content.ReadFromJsonAsync<ApiResponse<AdminCategoryDto>>())!.Data!;

        // Act 1 — Update Cat 1 to an unused slug
        var updatedSlug = $"cat1-updated-{Guid.NewGuid():N}";
        var updateReq = CreateAuthenticatedRequest(
            HttpMethod.Put,
            $"/api/admin/categories/{cat1.Id}",
            authCookie,
            antiforgeryCookie,
            csrfToken,
            new UpdateCategoryRequest { Name = "Cat 1 Renamed", Slug = updatedSlug, Description = "New Desc", IsActive = true });

        var updateResp = await _client.SendAsync(updateReq);
        Assert.Equal(HttpStatusCode.OK, updateResp.StatusCode);
        var updateResult = await updateResp.Content.ReadFromJsonAsync<ApiResponse<AdminCategoryDto>>();
        Assert.Equal(updatedSlug, updateResult!.Data!.Slug);
        Assert.Equal("Cat 1 Renamed", updateResult.Data.Name);

        // Act 2 — Attempt to update Cat 1 to Cat 2's slug (collision)
        var collisionReq = CreateAuthenticatedRequest(
            HttpMethod.Put,
            $"/api/admin/categories/{cat1.Id}",
            authCookie,
            antiforgeryCookie,
            csrfToken,
            new UpdateCategoryRequest { Name = "Cat 1", Slug = slug2 });

        var collisionResp = await _client.SendAsync(collisionReq);

        // Assert 2
        Assert.Equal(HttpStatusCode.Conflict, collisionResp.StatusCode);
    }

    [Fact]
    public async Task Admin_UpdateCategoryStatus_TogglesActiveState()
    {
        // Arrange
        var (authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync();
        var slug = $"cat-toggle-{Guid.NewGuid():N}";

        var create = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Post, "/api/admin/categories", authCookie, antiforgeryCookie, csrfToken,
            new CreateCategoryRequest { Name = "Toggle Category", Slug = slug, IsActive = true }));
        var cat = (await create.Content.ReadFromJsonAsync<ApiResponse<AdminCategoryDto>>())!.Data!;

        // Act — Soft disable the category via PATCH
        var patchReq = CreateAuthenticatedRequest(
            HttpMethod.Patch,
            $"/api/admin/categories/{cat.Id}/status",
            authCookie,
            antiforgeryCookie,
            csrfToken,
            new UpdateCategoryStatusRequest { IsActive = false });

        var patchResp = await _client.SendAsync(patchReq);
        Assert.Equal(HttpStatusCode.OK, patchResp.StatusCode);
        var patchResult = await patchResp.Content.ReadFromJsonAsync<ApiResponse<AdminCategoryDto>>();
        Assert.False(patchResult!.Data!.IsActive);

        // Public API verification: Category is now hidden from public GET /api/categories
        var publicResp = await _client.GetAsync("/api/categories");
        var publicResult = await publicResp.Content.ReadFromJsonAsync<ApiResponse<IReadOnlyList<CategoryDto>>>();
        Assert.NotNull(publicResult);
        Assert.DoesNotContain(publicResult.Data!, c => c.Slug == slug);
    }

    [Fact]
    public async Task Admin_CreateProduct_Success()
    {
        // Arrange
        var (authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync();
        var catSlug = $"cat-prod-{Guid.NewGuid():N}";

        var createCat = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Post, "/api/admin/categories", authCookie, antiforgeryCookie, csrfToken,
            new CreateCategoryRequest { Name = "Prod Category", Slug = catSlug }));
        var cat = (await createCat.Content.ReadFromJsonAsync<ApiResponse<AdminCategoryDto>>())!.Data!;

        var sku = $"CHATGPT_{Guid.NewGuid():N}".ToUpperInvariant();
        var productRequest = new CreateProductRequest
        {
            InternalSku = sku,
            Name = "ChatGPT Plus 1 Month",
            Description = "Premium AI subscription voucher",
            CategoryId = cat.Id,
            SellingPrice = 5500.00m,
            SellingCurrency = "PKR",
            IsActive = true
        };

        // Act
        var postReq = CreateAuthenticatedRequest(
            HttpMethod.Post,
            "/api/admin/products",
            authCookie,
            antiforgeryCookie,
            csrfToken,
            productRequest);

        var response = await _client.SendAsync(postReq);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<AdminProductDto>>();
        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.Equal(sku, result.Data!.InternalSku);
        Assert.Equal(5500.00m, result.Data.SellingPrice);
        Assert.Equal("PKR", result.Data.SellingCurrency);
        Assert.Equal(cat.Id, result.Data.CategoryId);
        Assert.True(result.Data.Id > 0);

        // Verify retrieval via GET /api/admin/products/{id}
        var getReq = CreateAuthenticatedRequest(
            HttpMethod.Get,
            $"/api/admin/products/{result.Data.Id}",
            authCookie,
            antiforgeryCookie,
            csrfToken);
        var getResp = await _client.SendAsync(getReq);
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
    }

    [Fact]
    public async Task Admin_CreateProduct_DuplicateSku_Returns409Conflict()
    {
        // Arrange
        var (authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync();
        var catSlug = $"cat-dupsku-{Guid.NewGuid():N}";

        var createCat = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Post, "/api/admin/categories", authCookie, antiforgeryCookie, csrfToken,
            new CreateCategoryRequest { Name = "SKU Category", Slug = catSlug }));
        var cat = (await createCat.Content.ReadFromJsonAsync<ApiResponse<AdminCategoryDto>>())!.Data!;

        var sku = $"DUPSKU_{Guid.NewGuid():N}".ToUpperInvariant();

        var firstReq = CreateAuthenticatedRequest(
            HttpMethod.Post, "/api/admin/products", authCookie, antiforgeryCookie, csrfToken,
            new CreateProductRequest
            {
                InternalSku = sku,
                Name = "First Product",
                CategoryId = cat.Id,
                SellingPrice = 1000m
            });
        var firstResp = await _client.SendAsync(firstReq);
        Assert.Equal(HttpStatusCode.Created, firstResp.StatusCode);

        // Act — create another product with the identical SKU
        var secondReq = CreateAuthenticatedRequest(
            HttpMethod.Post, "/api/admin/products", authCookie, antiforgeryCookie, csrfToken,
            new CreateProductRequest
            {
                InternalSku = sku,
                Name = "Duplicate Product",
                CategoryId = cat.Id,
                SellingPrice = 2000m
            });
        var secondResp = await _client.SendAsync(secondReq);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, secondResp.StatusCode);
        var result = await secondResp.Content.ReadFromJsonAsync<ApiResponse<AdminProductDto>>();
        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Contains("already exists", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Admin_CreateProduct_InvalidPrice_Returns400BadRequest()
    {
        // Arrange
        var (authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync();
        var catSlug = $"cat-price-{Guid.NewGuid():N}";

        var createCat = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Post, "/api/admin/categories", authCookie, antiforgeryCookie, csrfToken,
            new CreateCategoryRequest { Name = "Price Cat", Slug = catSlug }));
        var cat = (await createCat.Content.ReadFromJsonAsync<ApiResponse<AdminCategoryDto>>())!.Data!;

        // Act — attempt to create a product with negative price
        var req = CreateAuthenticatedRequest(
            HttpMethod.Post, "/api/admin/products", authCookie, antiforgeryCookie, csrfToken,
            new CreateProductRequest
            {
                InternalSku = $"NEGPRICE_{Guid.NewGuid():N}".ToUpperInvariant(),
                Name = "Negative Price Product",
                CategoryId = cat.Id,
                SellingPrice = -150.00m
            });
        var response = await _client.SendAsync(req);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Admin_CreateProduct_NonExistentCategory_Returns400BadRequest()
    {
        // Arrange
        var (authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync();

        // Act — attempt to create a product referencing category ID 999999
        var req = CreateAuthenticatedRequest(
            HttpMethod.Post, "/api/admin/products", authCookie, antiforgeryCookie, csrfToken,
            new CreateProductRequest
            {
                InternalSku = $"NOCAT_{Guid.NewGuid():N}".ToUpperInvariant(),
                Name = "Orphan Product",
                CategoryId = 999999,
                SellingPrice = 2500m
            });
        var response = await _client.SendAsync(req);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<AdminProductDto>>();
        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Contains("does not exist", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Admin_UpdateProduct_Success_PreservesSkuImmutability()
    {
        // Arrange
        var (authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync();
        var catSlug = $"cat-upd-{Guid.NewGuid():N}";

        var createCat = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Post, "/api/admin/categories", authCookie, antiforgeryCookie, csrfToken,
            new CreateCategoryRequest { Name = "Upd Category", Slug = catSlug }));
        var cat = (await createCat.Content.ReadFromJsonAsync<ApiResponse<AdminCategoryDto>>())!.Data!;

        var originalSku = $"ORIG_SKU_{Guid.NewGuid():N}".ToUpperInvariant();
        var createProd = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Post, "/api/admin/products", authCookie, antiforgeryCookie, csrfToken,
            new CreateProductRequest
            {
                InternalSku = originalSku,
                Name = "Original Product Name",
                CategoryId = cat.Id,
                SellingPrice = 3000m
            }));
        var prod = (await createProd.Content.ReadFromJsonAsync<ApiResponse<AdminProductDto>>())!.Data!;

        // Act — Update product details via PUT
        var updateReq = CreateAuthenticatedRequest(
            HttpMethod.Put,
            $"/api/admin/products/{prod.Id}",
            authCookie,
            antiforgeryCookie,
            csrfToken,
            new UpdateProductRequest
            {
                Name = "Updated Product Name",
                Description = "Updated Description",
                CategoryId = cat.Id,
                SellingPrice = 3500m,
                SellingCurrency = "PKR",
                IsActive = true
            });

        var updateResp = await _client.SendAsync(updateReq);

        // Assert
        Assert.Equal(HttpStatusCode.OK, updateResp.StatusCode);
        var updated = (await updateResp.Content.ReadFromJsonAsync<ApiResponse<AdminProductDto>>())!.Data!;
        Assert.Equal("Updated Product Name", updated.Name);
        Assert.Equal(3500m, updated.SellingPrice);
        Assert.Equal(originalSku, updated.InternalSku); // SKU remains untouched
    }

    [Fact]
    public async Task Admin_UpdateProductStatus_TogglesActive_AndHidesFromPublic()
    {
        // Arrange
        var (authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync();
        var catSlug = $"cat-prodstat-{Guid.NewGuid():N}";

        var createCat = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Post, "/api/admin/categories", authCookie, antiforgeryCookie, csrfToken,
            new CreateCategoryRequest { Name = "Prod Status Category", Slug = catSlug, IsActive = true }));
        var cat = (await createCat.Content.ReadFromJsonAsync<ApiResponse<AdminCategoryDto>>())!.Data!;

        var sku = $"STAT_SKU_{Guid.NewGuid():N}".ToUpperInvariant();
        var createProd = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Post, "/api/admin/products", authCookie, antiforgeryCookie, csrfToken,
            new CreateProductRequest
            {
                InternalSku = sku,
                Name = "Active Product",
                CategoryId = cat.Id,
                SellingPrice = 1200m,
                IsActive = true
            }));
        var prod = (await createProd.Content.ReadFromJsonAsync<ApiResponse<AdminProductDto>>())!.Data!;

        // Public check 1 — active product is found publicly
        var publicResp1 = await _client.GetAsync($"/api/products/{sku}");
        Assert.Equal(HttpStatusCode.OK, publicResp1.StatusCode);

        // Act — soft disable the product via PATCH
        var patchReq = CreateAuthenticatedRequest(
            HttpMethod.Patch,
            $"/api/admin/products/{prod.Id}/status",
            authCookie,
            antiforgeryCookie,
            csrfToken,
            new UpdateProductStatusRequest { IsActive = false });

        var patchResp = await _client.SendAsync(patchReq);
        Assert.Equal(HttpStatusCode.OK, patchResp.StatusCode);
        var patched = (await patchResp.Content.ReadFromJsonAsync<ApiResponse<AdminProductDto>>())!.Data!;
        Assert.False(patched.IsActive);

        // Public check 2 — disabled product returns 404 Not Found publicly
        var publicResp2 = await _client.GetAsync($"/api/products/{sku}");
        Assert.Equal(HttpStatusCode.NotFound, publicResp2.StatusCode);
    }

    [Fact]
    public async Task CategoryDisabled_HidesItsActiveProductsFromPublicApi()
    {
        // Arrange
        var (authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync();
        var catSlug = $"cat-cascadetest-{Guid.NewGuid():N}";

        var createCat = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Post, "/api/admin/categories", authCookie, antiforgeryCookie, csrfToken,
            new CreateCategoryRequest { Name = "Cascade Cat", Slug = catSlug, IsActive = true }));
        var cat = (await createCat.Content.ReadFromJsonAsync<ApiResponse<AdminCategoryDto>>())!.Data!;

        var sku = $"CASCADE_SKU_{Guid.NewGuid():N}".ToUpperInvariant();
        var createProd = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Post, "/api/admin/products", authCookie, antiforgeryCookie, csrfToken,
            new CreateProductRequest
            {
                InternalSku = sku,
                Name = "Cascade Test Product",
                CategoryId = cat.Id,
                SellingPrice = 4000m,
                IsActive = true // Product itself is active
            }));
        Assert.Equal(HttpStatusCode.Created, createProd.StatusCode);

        // Public check 1 — visible while category is active
        var pubResp1 = await _client.GetAsync($"/api/products/{sku}");
        Assert.Equal(HttpStatusCode.OK, pubResp1.StatusCode);

        // Act — disable the parent category
        var disableCat = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Patch,
            $"/api/admin/categories/{cat.Id}/status",
            authCookie,
            antiforgeryCookie,
            csrfToken,
            new UpdateCategoryStatusRequest { IsActive = false }));
        Assert.Equal(HttpStatusCode.OK, disableCat.StatusCode);

        // Public check 2 — product is now hidden from public GET /api/products/{sku}
        var pubResp2 = await _client.GetAsync($"/api/products/{sku}");
        Assert.Equal(HttpStatusCode.NotFound, pubResp2.StatusCode);

        // Public check 3 — product is excluded from public search listings
        var pubListResp = await _client.GetAsync($"/api/products?search={sku}");
        var pubListResult = await pubListResp.Content.ReadFromJsonAsync<ApiResponse<PagedResult<ProductSummaryDto>>>();
        Assert.NotNull(pubListResult);
        Assert.DoesNotContain(pubListResult.Data!.Items, p => p.InternalSku == sku);
    }

    [Fact]
    public async Task AnonymousUser_CannotModifyProductsOrCategories_Returns401()
    {
        // Act 1 — Anonymous attempt to create a category
        var catResp = await _client.PostAsJsonAsync("/api/admin/categories", new CreateCategoryRequest
        {
            Name = "Anon Category",
            Slug = "anon-cat"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, catResp.StatusCode);

        // Act 2 — Anonymous attempt to create a product
        var prodResp = await _client.PostAsJsonAsync("/api/admin/products", new CreateProductRequest
        {
            InternalSku = "ANON_PROD",
            Name = "Anon Prod",
            CategoryId = 1,
            SellingPrice = 100
        });
        Assert.Equal(HttpStatusCode.Unauthorized, prodResp.StatusCode);

        // Act 3 — Anonymous attempt to access admin category list
        var listResp = await _client.GetAsync("/api/admin/categories");
        Assert.Equal(HttpStatusCode.Unauthorized, listResp.StatusCode);
    }

    [Fact]
    public async Task NonAdminUser_CannotAccessOrModify_ReturnsUnauthorized()
    {
        // Arrange — Create a user, log in, then demote from Admin
        var email = $"demoted_catalog_{Guid.NewGuid():N}@test.local";
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

        // Demote the user (remove from Admin role)
        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var dbUser = await userManager.FindByIdAsync(user.Id);
            Assert.NotNull(dbUser);
            await userManager.RemoveFromRoleAsync(dbUser, AppRoles.Admin);
        }

        // Act — Attempt to access admin categories
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/admin/categories");
        req.Headers.Add("Cookie", authCookie);
        var resp = await _client.SendAsync(req);

        // Assert — Session validation rejects non-admin users
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Admin_MutativeRequests_RequireValidCsrfToken()
    {
        // Arrange
        var (authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync();

        // Act 1 — Attempt POST /api/admin/categories WITHOUT CSRF header
        var forgedReq = new HttpRequestMessage(HttpMethod.Post, "/api/admin/categories");
        forgedReq.Headers.Add("Cookie", authCookie);
        forgedReq.Content = JsonContent.Create(new CreateCategoryRequest
        {
            Name = "Forged Category",
            Slug = $"forged-{Guid.NewGuid():N}"
        });
        var forgedResp = await _client.SendAsync(forgedReq);

        // Assert 1 — AutoValidateAntiforgeryToken rejects with 400 Bad Request
        Assert.Equal(HttpStatusCode.BadRequest, forgedResp.StatusCode);

        // Act 2 — Legitimate request with valid CSRF header and cookie succeeds
        var legitSlug = $"legit-{Guid.NewGuid():N}";
        var legitReq = CreateAuthenticatedRequest(
            HttpMethod.Post,
            "/api/admin/categories",
            authCookie,
            antiforgeryCookie,
            csrfToken,
            new CreateCategoryRequest
            {
                Name = "Legit Category",
                Slug = legitSlug
            });
        var legitResp = await _client.SendAsync(legitReq);

        // Assert 2
        Assert.Equal(HttpStatusCode.Created, legitResp.StatusCode);
    }

    [Fact]
    public async Task Admin_FilterAndSearchProducts_ReturnsActiveAndInactive()
    {
        // Arrange
        var (authCookie, antiforgeryCookie, csrfToken) = await LoginAsAdminAsync();
        var catSlug = $"cat-filtertest-{Guid.NewGuid():N}";

        var createCat = await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Post, "/api/admin/categories", authCookie, antiforgeryCookie, csrfToken,
            new CreateCategoryRequest { Name = "Filter Test Cat", Slug = catSlug }));
        var cat = (await createCat.Content.ReadFromJsonAsync<ApiResponse<AdminCategoryDto>>())!.Data!;

        var activeSku = $"FILTER_ACT_{Guid.NewGuid():N}".ToUpperInvariant();
        var inactiveSku = $"FILTER_INACT_{Guid.NewGuid():N}".ToUpperInvariant();

        // Create active product
        await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Post, "/api/admin/products", authCookie, antiforgeryCookie, csrfToken,
            new CreateProductRequest
            {
                InternalSku = activeSku,
                Name = "Active Searchable Product",
                CategoryId = cat.Id,
                SellingPrice = 1000m,
                IsActive = true
            }));

        // Create inactive product
        await _client.SendAsync(CreateAuthenticatedRequest(
            HttpMethod.Post, "/api/admin/products", authCookie, antiforgeryCookie, csrfToken,
            new CreateProductRequest
            {
                InternalSku = inactiveSku,
                Name = "Inactive Searchable Product",
                CategoryId = cat.Id,
                SellingPrice = 2000m,
                IsActive = false
            }));

        // Act 1 — Admin requests all products in category (no isActive filter)
        var allReq = CreateAuthenticatedRequest(
            HttpMethod.Get,
            $"/api/admin/products?categoryId={cat.Id}",
            authCookie,
            antiforgeryCookie,
            csrfToken);
        var allResp = await _client.SendAsync(allReq);
        Assert.Equal(HttpStatusCode.OK, allResp.StatusCode);
        var allResult = await allResp.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AdminProductDto>>>();
        Assert.NotNull(allResult);
        Assert.Contains(allResult.Data!.Items, p => p.InternalSku == activeSku);
        Assert.Contains(allResult.Data.Items, p => p.InternalSku == inactiveSku);

        // Act 2 — Admin requests inactive products only
        var inactReq = CreateAuthenticatedRequest(
            HttpMethod.Get,
            $"/api/admin/products?categoryId={cat.Id}&isActive=false",
            authCookie,
            antiforgeryCookie,
            csrfToken);
        var inactResp = await _client.SendAsync(inactReq);
        Assert.Equal(HttpStatusCode.OK, inactResp.StatusCode);
        var inactResult = await inactResp.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AdminProductDto>>>();
        Assert.NotNull(inactResult);
        Assert.DoesNotContain(inactResult.Data!.Items, p => p.InternalSku == activeSku);
        Assert.Contains(inactResult.Data.Items, p => p.InternalSku == inactiveSku);

        // Act 3 — Admin searches by keyword
        var searchReq = CreateAuthenticatedRequest(
            HttpMethod.Get,
            $"/api/admin/products?search={activeSku}",
            authCookie,
            antiforgeryCookie,
            csrfToken);
        var searchResp = await _client.SendAsync(searchReq);
        Assert.Equal(HttpStatusCode.OK, searchResp.StatusCode);
        var searchResult = await searchResp.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AdminProductDto>>>();
        Assert.NotNull(searchResult);
        Assert.Single(searchResult.Data!.Items);
        Assert.Equal(activeSku, searchResult.Data.Items[0].InternalSku);
    }
}
