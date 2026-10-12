using Microsoft.EntityFrameworkCore;
using WebApiTgBot.Common.Models;
using WebApiTgBot.Infrastructure.Data;
using WebApiTgBot.Modules.Catalog.Models;
using WebApiTgBot.Modules.Catalog.Models.DTOs.Admin;

namespace WebApiTgBot.Modules.Catalog.Services;

/// <summary>
/// Implements business logic for administrative category and product management.
/// Enforces uniqueness constraints, category validation, audit timestamps, and non-destructive management.
/// </summary>
public class AdminCatalogService : IAdminCatalogService
{
    private readonly AppDbContext _dbContext;

    public AdminCatalogService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // ── Categories ─────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<AdminCategoryDto>> GetCategoriesAsync(
        bool? isActive = null,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.Categories.AsNoTracking().AsQueryable();

        if (isActive.HasValue)
        {
            query = query.Where(c => c.IsActive == isActive.Value);
        }

        return await query
            .OrderBy(c => c.Name)
            .Select(c => new AdminCategoryDto
            {
                Id = c.Id,
                Name = c.Name,
                Slug = c.Slug,
                Description = c.Description,
                IsActive = c.IsActive,
                ProductCount = c.Products.Count,
                CreatedAt = c.CreatedAt,
                UpdatedAt = c.UpdatedAt
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<AdminCatalogResult<AdminCategoryDto>> GetCategoryByIdAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        var category = await _dbContext.Categories
            .AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new AdminCategoryDto
            {
                Id = c.Id,
                Name = c.Name,
                Slug = c.Slug,
                Description = c.Description,
                IsActive = c.IsActive,
                ProductCount = c.Products.Count,
                CreatedAt = c.CreatedAt,
                UpdatedAt = c.UpdatedAt
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (category == null)
        {
            return AdminCatalogResult<AdminCategoryDto>.NotFound($"Category with ID {id} was not found.");
        }

        return AdminCatalogResult<AdminCategoryDto>.Ok(category);
    }

    public async Task<AdminCatalogResult<AdminCategoryDto>> CreateCategoryAsync(
        CreateCategoryRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalizedSlug = request.Slug.Trim().ToLowerInvariant();

        var slugExists = await _dbContext.Categories
            .AnyAsync(c => c.Slug.ToLower() == normalizedSlug, cancellationToken);

        if (slugExists)
        {
            return AdminCatalogResult<AdminCategoryDto>.Conflict($"A category with slug '{normalizedSlug}' already exists.");
        }

        var now = DateTime.UtcNow;
        var category = new Category
        {
            Name = request.Name.Trim(),
            Slug = normalizedSlug,
            Description = request.Description?.Trim(),
            IsActive = request.IsActive,
            CreatedAt = now,
            UpdatedAt = now
        };

        _dbContext.Categories.Add(category);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var dto = new AdminCategoryDto
        {
            Id = category.Id,
            Name = category.Name,
            Slug = category.Slug,
            Description = category.Description,
            IsActive = category.IsActive,
            ProductCount = 0,
            CreatedAt = category.CreatedAt,
            UpdatedAt = category.UpdatedAt
        };

        return AdminCatalogResult<AdminCategoryDto>.Ok(dto, "Category created successfully.");
    }

    public async Task<AdminCatalogResult<AdminCategoryDto>> UpdateCategoryAsync(
        int id,
        UpdateCategoryRequest request,
        CancellationToken cancellationToken = default)
    {
        var category = await _dbContext.Categories
            .Include(c => c.Products)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

        if (category == null)
        {
            return AdminCatalogResult<AdminCategoryDto>.NotFound($"Category with ID {id} was not found.");
        }

        var normalizedSlug = request.Slug.Trim().ToLowerInvariant();

        var slugExists = await _dbContext.Categories
            .AnyAsync(c => c.Slug.ToLower() == normalizedSlug && c.Id != id, cancellationToken);

        if (slugExists)
        {
            return AdminCatalogResult<AdminCategoryDto>.Conflict($"A category with slug '{normalizedSlug}' already exists.");
        }

        category.Name = request.Name.Trim();
        category.Slug = normalizedSlug;
        category.Description = request.Description?.Trim();
        category.IsActive = request.IsActive;
        category.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);

        var dto = new AdminCategoryDto
        {
            Id = category.Id,
            Name = category.Name,
            Slug = category.Slug,
            Description = category.Description,
            IsActive = category.IsActive,
            ProductCount = category.Products.Count,
            CreatedAt = category.CreatedAt,
            UpdatedAt = category.UpdatedAt
        };

        return AdminCatalogResult<AdminCategoryDto>.Ok(dto, "Category updated successfully.");
    }

    public async Task<AdminCatalogResult<AdminCategoryDto>> UpdateCategoryStatusAsync(
        int id,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        var category = await _dbContext.Categories
            .Include(c => c.Products)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

        if (category == null)
        {
            return AdminCatalogResult<AdminCategoryDto>.NotFound($"Category with ID {id} was not found.");
        }

        category.IsActive = isActive;
        category.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);

        var dto = new AdminCategoryDto
        {
            Id = category.Id,
            Name = category.Name,
            Slug = category.Slug,
            Description = category.Description,
            IsActive = category.IsActive,
            ProductCount = category.Products.Count,
            CreatedAt = category.CreatedAt,
            UpdatedAt = category.UpdatedAt
        };

        return AdminCatalogResult<AdminCategoryDto>.Ok(dto, $"Category status updated to {(isActive ? "active" : "inactive")}.");
    }

    // ── Products ───────────────────────────────────────────────────────────────

    public async Task<PagedResult<AdminProductDto>> GetProductsAsync(
        AdminProductFilterQuery query,
        CancellationToken cancellationToken = default)
    {
        var safePage = query.Page < 1 ? 1 : query.Page;
        var safePageSize = query.PageSize < 1 ? 20 : Math.Min(query.PageSize, 100);

        var dbQuery = _dbContext.Products
            .AsNoTracking()
            .Include(p => p.Category)
            .AsQueryable();

        if (query.CategoryId.HasValue)
        {
            dbQuery = dbQuery.Where(p => p.CategoryId == query.CategoryId.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.CategorySlug))
        {
            var normalizedSlug = query.CategorySlug.Trim().ToLowerInvariant();
            dbQuery = dbQuery.Where(p => p.Category.Slug.ToLower() == normalizedSlug);
        }

        if (query.IsActive.HasValue)
        {
            dbQuery = dbQuery.Where(p => p.IsActive == query.IsActive.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim().ToLowerInvariant();
            dbQuery = dbQuery.Where(p =>
                p.Name.ToLower().Contains(search) ||
                p.InternalSku.ToLower().Contains(search) ||
                (p.Description != null && p.Description.ToLower().Contains(search)));
        }

        var totalCount = await dbQuery.CountAsync(cancellationToken);

        var items = await dbQuery
            .OrderBy(p => p.Name)
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .Select(p => new AdminProductDto
            {
                Id = p.Id,
                InternalSku = p.InternalSku,
                Name = p.Name,
                Description = p.Description,
                CategoryId = p.CategoryId,
                CategoryName = p.Category.Name,
                CategorySlug = p.Category.Slug,
                CategoryIsActive = p.Category.IsActive,
                SellingPrice = p.SellingPrice,
                SellingCurrency = p.SellingCurrency,
                IsActive = p.IsActive,
                CreatedAt = p.CreatedAt,
                UpdatedAt = p.UpdatedAt
            })
            .ToListAsync(cancellationToken);

        return new PagedResult<AdminProductDto>(items, safePage, safePageSize, totalCount);
    }

    public async Task<AdminCatalogResult<AdminProductDto>> GetProductByIdAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        var product = await _dbContext.Products
            .AsNoTracking()
            .Include(p => p.Category)
            .Where(p => p.Id == id)
            .Select(p => new AdminProductDto
            {
                Id = p.Id,
                InternalSku = p.InternalSku,
                Name = p.Name,
                Description = p.Description,
                CategoryId = p.CategoryId,
                CategoryName = p.Category.Name,
                CategorySlug = p.Category.Slug,
                CategoryIsActive = p.Category.IsActive,
                SellingPrice = p.SellingPrice,
                SellingCurrency = p.SellingCurrency,
                IsActive = p.IsActive,
                CreatedAt = p.CreatedAt,
                UpdatedAt = p.UpdatedAt
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (product == null)
        {
            return AdminCatalogResult<AdminProductDto>.NotFound($"Product with ID {id} was not found.");
        }

        return AdminCatalogResult<AdminProductDto>.Ok(product);
    }

    public async Task<AdminCatalogResult<AdminProductDto>> CreateProductAsync(
        CreateProductRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.SellingPrice < 0)
        {
            return AdminCatalogResult<AdminProductDto>.BadRequest("Product selling price cannot be negative.");
        }

        var category = await _dbContext.Categories
            .FirstOrDefaultAsync(c => c.Id == request.CategoryId, cancellationToken);

        if (category == null)
        {
            return AdminCatalogResult<AdminProductDto>.BadRequest($"Category with ID {request.CategoryId} does not exist.");
        }

        var normalizedSku = request.InternalSku.Trim().ToUpperInvariant();

        var skuExists = await _dbContext.Products
            .AnyAsync(p => p.InternalSku.ToLower() == normalizedSku.ToLower(), cancellationToken);

        if (skuExists)
        {
            return AdminCatalogResult<AdminProductDto>.Conflict($"A product with internal SKU '{normalizedSku}' already exists.");
        }

        var currency = string.IsNullOrWhiteSpace(request.SellingCurrency)
            ? "PKR"
            : request.SellingCurrency.Trim().ToUpperInvariant();

        var now = DateTime.UtcNow;
        var product = new Product
        {
            InternalSku = normalizedSku,
            Name = request.Name.Trim(),
            Description = request.Description?.Trim(),
            CategoryId = category.Id,
            SellingPrice = request.SellingPrice,
            SellingCurrency = currency,
            IsActive = request.IsActive,
            CreatedAt = now,
            UpdatedAt = now
        };

        _dbContext.Products.Add(product);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var dto = new AdminProductDto
        {
            Id = product.Id,
            InternalSku = product.InternalSku,
            Name = product.Name,
            Description = product.Description,
            CategoryId = product.CategoryId,
            CategoryName = category.Name,
            CategorySlug = category.Slug,
            CategoryIsActive = category.IsActive,
            SellingPrice = product.SellingPrice,
            SellingCurrency = product.SellingCurrency,
            IsActive = product.IsActive,
            CreatedAt = product.CreatedAt,
            UpdatedAt = product.UpdatedAt
        };

        return AdminCatalogResult<AdminProductDto>.Ok(dto, "Product created successfully.");
    }

    public async Task<AdminCatalogResult<AdminProductDto>> UpdateProductAsync(
        int id,
        UpdateProductRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.SellingPrice < 0)
        {
            return AdminCatalogResult<AdminProductDto>.BadRequest("Product selling price cannot be negative.");
        }

        var product = await _dbContext.Products
            .Include(p => p.Category)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (product == null)
        {
            return AdminCatalogResult<AdminProductDto>.NotFound($"Product with ID {id} was not found.");
        }

        var category = await _dbContext.Categories
            .FirstOrDefaultAsync(c => c.Id == request.CategoryId, cancellationToken);

        if (category == null)
        {
            return AdminCatalogResult<AdminProductDto>.BadRequest($"Category with ID {request.CategoryId} does not exist.");
        }

        var currency = string.IsNullOrWhiteSpace(request.SellingCurrency)
            ? "PKR"
            : request.SellingCurrency.Trim().ToUpperInvariant();

        // Note: InternalSku is deliberately preserved and immutable.
        product.Name = request.Name.Trim();
        product.Description = request.Description?.Trim();
        product.CategoryId = category.Id;
        product.Category = category;
        product.SellingPrice = request.SellingPrice;
        product.SellingCurrency = currency;
        product.IsActive = request.IsActive;
        product.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);

        var dto = new AdminProductDto
        {
            Id = product.Id,
            InternalSku = product.InternalSku,
            Name = product.Name,
            Description = product.Description,
            CategoryId = product.CategoryId,
            CategoryName = category.Name,
            CategorySlug = category.Slug,
            CategoryIsActive = category.IsActive,
            SellingPrice = product.SellingPrice,
            SellingCurrency = product.SellingCurrency,
            IsActive = product.IsActive,
            CreatedAt = product.CreatedAt,
            UpdatedAt = product.UpdatedAt
        };

        return AdminCatalogResult<AdminProductDto>.Ok(dto, "Product updated successfully.");
    }

    public async Task<AdminCatalogResult<AdminProductDto>> UpdateProductStatusAsync(
        int id,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        var product = await _dbContext.Products
            .Include(p => p.Category)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (product == null)
        {
            return AdminCatalogResult<AdminProductDto>.NotFound($"Product with ID {id} was not found.");
        }

        product.IsActive = isActive;
        product.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);

        var dto = new AdminProductDto
        {
            Id = product.Id,
            InternalSku = product.InternalSku,
            Name = product.Name,
            Description = product.Description,
            CategoryId = product.CategoryId,
            CategoryName = product.Category.Name,
            CategorySlug = product.Category.Slug,
            CategoryIsActive = product.Category.IsActive,
            SellingPrice = product.SellingPrice,
            SellingCurrency = product.SellingCurrency,
            IsActive = product.IsActive,
            CreatedAt = product.CreatedAt,
            UpdatedAt = product.UpdatedAt
        };

        return AdminCatalogResult<AdminProductDto>.Ok(dto, $"Product status updated to {(isActive ? "active" : "inactive")}.");
    }
}
