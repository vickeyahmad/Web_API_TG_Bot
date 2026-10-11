using Microsoft.EntityFrameworkCore;
using WebApiTgBot.Common.Models;
using WebApiTgBot.Infrastructure.Data;
using WebApiTgBot.Modules.Catalog.Models.DTOs;

namespace WebApiTgBot.Modules.Catalog.Services;

/// <summary>
/// Implements business logic for public catalog operations.
/// Strictly enforces that inactive categories, inactive products,
/// and vendor details are never exposed to the public.
/// </summary>
public class CatalogService : ICatalogService
{
    private readonly AppDbContext _dbContext;

    public CatalogService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<CategoryDto>> GetActiveCategoriesAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext.Categories
            .AsNoTracking()
            .Where(c => c.IsActive)
            .OrderBy(c => c.Name)
            .Select(c => new CategoryDto
            {
                Id = c.Id,
                Name = c.Name,
                Slug = c.Slug,
                Description = c.Description
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<PagedResult<ProductSummaryDto>> GetActiveProductsAsync(
        string? categorySlug = null,
        string? search = null,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        // Enforce safe pagination boundaries
        var safePage = page < 1 ? 1 : page;
        var safePageSize = pageSize < 1 ? 20 : Math.Min(pageSize, 100);

        // Core business rule: products must be active AND their category must be active
        var query = _dbContext.Products
            .AsNoTracking()
            .Include(p => p.Category)
            .Where(p => p.IsActive && p.Category.IsActive);

        // Optional category slug filter
        if (!string.IsNullOrWhiteSpace(categorySlug))
        {
            var normalizedSlug = categorySlug.Trim().ToLower();
            query = query.Where(p => p.Category.Slug.ToLower() == normalizedSlug);
        }

        // Optional search keyword filter
        if (!string.IsNullOrWhiteSpace(search))
        {
            var normalizedSearch = search.Trim().ToLower();
            query = query.Where(p =>
                p.Name.ToLower().Contains(normalizedSearch) ||
                p.InternalSku.ToLower().Contains(normalizedSearch) ||
                (p.Description != null && p.Description.ToLower().Contains(normalizedSearch)));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderBy(p => p.Name)
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .Select(p => new ProductSummaryDto
            {
                Id = p.Id,
                InternalSku = p.InternalSku,
                Name = p.Name,
                Description = p.Description,
                CategoryId = p.CategoryId,
                CategoryName = p.Category.Name,
                CategorySlug = p.Category.Slug,
                SellingPrice = p.SellingPrice,
                SellingCurrency = p.SellingCurrency
            })
            .ToListAsync(cancellationToken);

        return new PagedResult<ProductSummaryDto>(items, safePage, safePageSize, totalCount);
    }

    public async Task<ProductDetailDto?> GetProductBySkuAsync(string sku, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sku))
        {
            return null;
        }

        var normalizedSku = sku.Trim().ToLower();

        // Retrieve active product in an active category
        var product = await _dbContext.Products
            .AsNoTracking()
            .Include(p => p.Category)
            .Where(p => p.InternalSku.ToLower() == normalizedSku && p.IsActive && p.Category.IsActive)
            .FirstOrDefaultAsync(cancellationToken);

        if (product == null)
        {
            return null;
        }

        return new ProductDetailDto
        {
            Id = product.Id,
            InternalSku = product.InternalSku,
            Name = product.Name,
            Description = product.Description,
            CategoryId = product.CategoryId,
            CategoryName = product.Category.Name,
            CategorySlug = product.Category.Slug,
            SellingPrice = product.SellingPrice,
            SellingCurrency = product.SellingCurrency,
            CreatedAt = product.CreatedAt,
            UpdatedAt = product.UpdatedAt
        };
    }
}
