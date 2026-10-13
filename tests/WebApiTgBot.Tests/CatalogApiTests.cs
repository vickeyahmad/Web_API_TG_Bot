using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using WebApiTgBot.Common.Models;
using WebApiTgBot.Infrastructure.Data;
using WebApiTgBot.Modules.Catalog.Models;
using WebApiTgBot.Modules.Catalog.Models.DTOs;

namespace WebApiTgBot.Tests;

/// <summary>
/// Integration tests for the public Catalog API endpoints.
/// Verifies category listing, product filtering, search, pagination,
/// SKU lookups, privacy boundaries, and vendor exclusion.
/// </summary>
public class CatalogApiTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public CatalogApiTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task SeedDatabaseAsync(Func<AppDbContext, Task> seedAction)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await seedAction(db);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task GetCategories_ReturnsActiveCategoriesOnly()
    {
        // Arrange — seed active and inactive categories
        var uniquePrefix = Guid.NewGuid().ToString("N")[..6];
        var activeCat = new Category
        {
            Name = $"AI Tools {uniquePrefix}",
            Slug = $"ai-tools-{uniquePrefix}",
            Description = "AI subscriptions",
            IsActive = true
        };
        var inactiveCat = new Category
        {
            Name = $"Old Category {uniquePrefix}",
            Slug = $"old-cat-{uniquePrefix}",
            Description = "Deprecated",
            IsActive = false
        };

        await SeedDatabaseAsync(async db =>
        {
            db.Categories.AddRange(activeCat, inactiveCat);
            await Task.CompletedTask;
        });

        // Act
        var response = await _client.GetAsync("/api/categories");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<List<CategoryDto>>>();

        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.Contains(result.Data!, c => c.Slug == activeCat.Slug);
        Assert.DoesNotContain(result.Data!, c => c.Slug == inactiveCat.Slug);
    }

    [Fact]
    public async Task GetProducts_ReturnsActiveProductsInActiveCategoriesOnly()
    {
        // Arrange
        var prefix = Guid.NewGuid().ToString("N")[..6];
        var activeCategory = new Category
        {
            Name = $"Active Cat {prefix}",
            Slug = $"active-cat-{prefix}",
            IsActive = true
        };
        var inactiveCategory = new Category
        {
            Name = $"Inactive Cat {prefix}",
            Slug = $"inactive-cat-{prefix}",
            IsActive = false
        };

        var visibleProduct = new Product
        {
            InternalSku = $"SKU_VISIBLE_{prefix}",
            Name = $"Visible Product {prefix}",
            Category = activeCategory,
            SellingPrice = 5000m,
            SellingCurrency = "PKR",
            IsActive = true
        };
        var inactiveProduct = new Product
        {
            InternalSku = $"SKU_INACTIVE_{prefix}",
            Name = $"Inactive Product {prefix}",
            Category = activeCategory,
            SellingPrice = 3000m,
            SellingCurrency = "PKR",
            IsActive = false
        };
        var hiddenCatProduct = new Product
        {
            InternalSku = $"SKU_HIDDEN_CAT_{prefix}",
            Name = $"Hidden Category Product {prefix}",
            Category = inactiveCategory,
            SellingPrice = 4000m,
            SellingCurrency = "PKR",
            IsActive = true
        };

        await SeedDatabaseAsync(async db =>
        {
            db.Categories.AddRange(activeCategory, inactiveCategory);
            db.Products.AddRange(visibleProduct, inactiveProduct, hiddenCatProduct);
            await Task.CompletedTask;
        });

        // Act
        var response = await _client.GetAsync($"/api/products?search={prefix}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<PagedResult<ProductSummaryDto>>>();

        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.Contains(result.Data!.Items, p => p.InternalSku == visibleProduct.InternalSku);
        Assert.DoesNotContain(result.Data!.Items, p => p.InternalSku == inactiveProduct.InternalSku);
        Assert.DoesNotContain(result.Data!.Items, p => p.InternalSku == hiddenCatProduct.InternalSku);
    }

    [Fact]
    public async Task GetProducts_FiltersByCategorySlugCorrectly()
    {
        // Arrange
        var prefix = Guid.NewGuid().ToString("N")[..6];
        var catAi = new Category { Name = $"AI {prefix}", Slug = $"ai-{prefix}", IsActive = true };
        var catSmm = new Category { Name = $"SMM {prefix}", Slug = $"smm-{prefix}", IsActive = true };

        var aiProduct = new Product
        {
            InternalSku = $"AI_PROD_{prefix}",
            Name = $"AI Tool {prefix}",
            Category = catAi,
            SellingPrice = 5000m,
            SellingCurrency = "PKR",
            IsActive = true
        };
        var smmProduct = new Product
        {
            InternalSku = $"SMM_PROD_{prefix}",
            Name = $"SMM Service {prefix}",
            Category = catSmm,
            SellingPrice = 1200m,
            SellingCurrency = "PKR",
            IsActive = true
        };

        await SeedDatabaseAsync(async db =>
        {
            db.Categories.AddRange(catAi, catSmm);
            db.Products.AddRange(aiProduct, smmProduct);
            await Task.CompletedTask;
        });

        // Act
        var response = await _client.GetAsync($"/api/products?category={catAi.Slug}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<PagedResult<ProductSummaryDto>>>();

        Assert.NotNull(result);
        Assert.Contains(result.Data!.Items, p => p.InternalSku == aiProduct.InternalSku);
        Assert.DoesNotContain(result.Data!.Items, p => p.InternalSku == smmProduct.InternalSku);
    }

    [Fact]
    public async Task GetProducts_Search_ReturnsMatchingProducts()
    {
        // Arrange
        var prefix = Guid.NewGuid().ToString("N")[..6];
        var category = new Category { Name = $"Category {prefix}", Slug = $"cat-{prefix}", IsActive = true };

        var gptProduct = new Product
        {
            InternalSku = $"GPT_{prefix}",
            Name = $"ChatGPT Plus {prefix}",
            Description = "AI Assistant subscription",
            Category = category,
            SellingPrice = 5000m,
            SellingCurrency = "PKR",
            IsActive = true
        };
        var canvaProduct = new Product
        {
            InternalSku = $"CANVA_{prefix}",
            Name = $"Canva Pro {prefix}",
            Description = "Graphic design tool",
            Category = category,
            SellingPrice = 2500m,
            SellingCurrency = "PKR",
            IsActive = true
        };

        await SeedDatabaseAsync(async db =>
        {
            db.Categories.Add(category);
            db.Products.AddRange(gptProduct, canvaProduct);
            await Task.CompletedTask;
        });

        // Act — search for "chatgpt" case-insensitively
        var response = await _client.GetAsync($"/api/products?search=chatgpt&category={category.Slug}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<PagedResult<ProductSummaryDto>>>();

        Assert.NotNull(result);
        Assert.Contains(result.Data!.Items, p => p.InternalSku == gptProduct.InternalSku);
        Assert.DoesNotContain(result.Data!.Items, p => p.InternalSku == canvaProduct.InternalSku);
    }

    [Fact]
    public async Task GetProducts_Pagination_CalculatesMetadataCorrectly()
    {
        // Arrange — seed 5 items
        var prefix = Guid.NewGuid().ToString("N")[..6];
        var category = new Category { Name = $"Paging {prefix}", Slug = $"paging-{prefix}", IsActive = true };

        var products = Enumerable.Range(1, 5).Select(i => new Product
        {
            InternalSku = $"PAGED_{prefix}_{i}",
            Name = $"Paged Product {prefix} {i}",
            Category = category,
            SellingPrice = 1000m * i,
            SellingCurrency = "PKR",
            IsActive = true
        }).ToList();

        await SeedDatabaseAsync(async db =>
        {
            db.Categories.Add(category);
            db.Products.AddRange(products);
            await Task.CompletedTask;
        });

        // Act — request page 1 with pageSize 2
        var responsePage1 = await _client.GetAsync($"/api/products?category={category.Slug}&page=1&pageSize=2");
        var resultPage1 = await responsePage1.Content.ReadFromJsonAsync<ApiResponse<PagedResult<ProductSummaryDto>>>();

        // Assert page 1
        Assert.NotNull(resultPage1);
        Assert.Equal(5, resultPage1.Data!.TotalCount);
        Assert.Equal(3, resultPage1.Data!.TotalPages);
        Assert.Equal(2, resultPage1.Data!.Items.Count);
        Assert.True(resultPage1.Data!.HasNextPage);
        Assert.False(resultPage1.Data!.HasPreviousPage);

        // Act — request page 2 with pageSize 2
        var responsePage2 = await _client.GetAsync($"/api/products?category={category.Slug}&page=2&pageSize=2");
        var resultPage2 = await responsePage2.Content.ReadFromJsonAsync<ApiResponse<PagedResult<ProductSummaryDto>>>();

        // Assert page 2
        Assert.NotNull(resultPage2);
        Assert.Equal(2, resultPage2.Data!.Items.Count);
        Assert.True(resultPage2.Data!.HasNextPage);
        Assert.True(resultPage2.Data!.HasPreviousPage);
    }

    [Fact]
    public async Task GetProductBySku_ReturnsProductDetails_WhenActive()
    {
        // Arrange
        var prefix = Guid.NewGuid().ToString("N")[..6];
        var category = new Category { Name = $"Detail {prefix}", Slug = $"detail-{prefix}", IsActive = true };
        var sku = $"CHATGPT_PLUS_1M_{prefix}";
        var product = new Product
        {
            InternalSku = sku,
            Name = "ChatGPT Plus — 1 Month",
            Description = "Official 1 month subscription",
            Category = category,
            SellingPrice = 5000.0000m,
            SellingCurrency = "PKR",
            IsActive = true
        };

        await SeedDatabaseAsync(async db =>
        {
            db.Categories.Add(category);
            db.Products.Add(product);
            await Task.CompletedTask;
        });

        // Act
        var response = await _client.GetAsync($"/api/products/{sku}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<ProductDetailDto>>();

        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.Equal(sku, result.Data!.InternalSku);
        Assert.Equal("ChatGPT Plus — 1 Month", result.Data!.Name);
        Assert.Equal(5000.0000m, result.Data!.SellingPrice);
        Assert.Equal("PKR", result.Data!.SellingCurrency);
        Assert.Equal(category.Slug, result.Data!.CategorySlug);
    }

    [Fact]
    public async Task GetProductBySku_ReturnsNotFound_WhenSkuDoesNotExist()
    {
        // Act
        var response = await _client.GetAsync("/api/products/NON_EXISTENT_SKU_9999");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<ProductDetailDto>>();
        Assert.NotNull(result);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task GetProductBySku_ReturnsNotFound_WhenProductIsInactive()
    {
        // Arrange
        var prefix = Guid.NewGuid().ToString("N")[..6];
        var category = new Category { Name = $"Inactive Prod Cat {prefix}", Slug = $"inact-cat-{prefix}", IsActive = true };
        var sku = $"INACTIVE_SKU_{prefix}";
        var product = new Product
        {
            InternalSku = sku,
            Name = "Inactive Product",
            Category = category,
            SellingPrice = 2000m,
            SellingCurrency = "PKR",
            IsActive = false // Inactive
        };

        await SeedDatabaseAsync(async db =>
        {
            db.Categories.Add(category);
            db.Products.Add(product);
            await Task.CompletedTask;
        });

        // Act
        var response = await _client.GetAsync($"/api/products/{sku}");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetProductBySku_ReturnsNotFound_WhenCategoryIsInactive()
    {
        // Arrange
        var prefix = Guid.NewGuid().ToString("N")[..6];
        var inactiveCat = new Category { Name = $"Disabled Cat {prefix}", Slug = $"disabled-{prefix}", IsActive = false };
        var sku = $"PROD_IN_DISABLED_CAT_{prefix}";
        var product = new Product
        {
            InternalSku = sku,
            Name = "Product in Disabled Category",
            Category = inactiveCat,
            SellingPrice = 2000m,
            SellingCurrency = "PKR",
            IsActive = true
        };

        await SeedDatabaseAsync(async db =>
        {
            db.Categories.Add(inactiveCat);
            db.Products.Add(product);
            await Task.CompletedTask;
        });

        // Act
        var response = await _client.GetAsync($"/api/products/{sku}");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Security_VendorDetailsAndCosts_NeverExposedInPublicResponses()
    {
        // Arrange — Create a product with a private Vendor and VendorProduct mapping
        var prefix = Guid.NewGuid().ToString("N")[..6];
        var secretVendorName = $"SecretSupplier_{prefix}";
        var secretExternalId = $"EXT_ID_SECRET_{prefix}";
        var sku = $"SECURE_SKU_{prefix}";

        var category = new Category { Name = $"Security Cat {prefix}", Slug = $"sec-{prefix}", IsActive = true };
        var product = new Product
        {
            InternalSku = sku,
            Name = $"Secure Tool {prefix}",
            Description = "Tool with hidden supplier",
            Category = category,
            SellingPrice = 5000m,
            SellingCurrency = "PKR",
            IsActive = true
        };

        var vendor = new Vendor
        {
            InternalName = secretVendorName,
            IsActive = true
        };

        var vendorProduct = new VendorProduct
        {
            Vendor = vendor,
            Product = product,
            ExternalProductId = secretExternalId,
            VendorCost = 98.7654m,
            VendorCostCurrency = "USDT",
            IsAvailable = true,
            Priority = 1
        };

        await SeedDatabaseAsync(async db =>
        {
            db.Categories.Add(category);
            db.Vendors.Add(vendor);
            db.Products.Add(product);
            db.VendorProducts.Add(vendorProduct);
            await Task.CompletedTask;
        });

        // Act 1: Call /api/products
        var listResponse = await _client.GetAsync($"/api/products?search={sku}");
        var listJson = await listResponse.Content.ReadAsStringAsync();

        // Act 2: Call /api/products/{sku}
        var detailResponse = await _client.GetAsync($"/api/products/{sku}");
        var detailJson = await detailResponse.Content.ReadAsStringAsync();

        // Assert: Verify neither response exposes ANY vendor data or vendor cost
        foreach (var json in new[] { listJson, detailJson })
        {
            // Supplier names and IDs must never appear
            Assert.DoesNotContain(secretVendorName, json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secretExternalId, json, StringComparison.OrdinalIgnoreCase);

            // Vendor costs and currencies must never appear
            Assert.DoesNotContain("98.7654", json);
            Assert.DoesNotContain("USDT", json);

            // Keys containing "vendor" must not appear in the public payload
            Assert.DoesNotContain("\"vendor\"", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"vendorCost\"", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"vendorId\"", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"externalProductId\"", json, StringComparison.OrdinalIgnoreCase);
        }
    }
}
