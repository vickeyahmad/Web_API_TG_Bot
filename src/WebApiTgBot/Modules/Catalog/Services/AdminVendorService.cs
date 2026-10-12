using Microsoft.EntityFrameworkCore;
using WebApiTgBot.Common.Models;
using WebApiTgBot.Infrastructure.Data;
using WebApiTgBot.Modules.Catalog.Models;
using WebApiTgBot.Modules.Catalog.Models.DTOs.Admin;

namespace WebApiTgBot.Modules.Catalog.Services;

/// <summary>
/// Implements administrative vendor management and vendor-product mapping business logic.
/// Enforces supplier isolation, unique constraints, referential integrity, multi-currency costs,
/// and non-destructive management.
/// </summary>
public class AdminVendorService : IAdminVendorService
{
    private readonly AppDbContext _dbContext;

    public AdminVendorService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // ── Vendor Operations ───────────────────────────────────────────────────────

    public async Task<PagedResult<AdminVendorDto>> GetVendorsAsync(
        AdminVendorFilterQuery query,
        CancellationToken cancellationToken = default)
    {
        var safePage = query.Page < 1 ? 1 : query.Page;
        var safePageSize = query.PageSize < 1 ? 20 : Math.Min(query.PageSize, 100);

        var dbQuery = _dbContext.Vendors
            .AsNoTracking()
            .Include(v => v.VendorProducts)
            .AsQueryable();

        if (query.IsActive.HasValue)
        {
            dbQuery = dbQuery.Where(v => v.IsActive == query.IsActive.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim().ToLowerInvariant();
            dbQuery = dbQuery.Where(v => v.InternalName.ToLower().Contains(search));
        }

        var totalCount = await dbQuery.CountAsync(cancellationToken);

        var items = await dbQuery
            .OrderBy(v => v.InternalName)
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .Select(v => new AdminVendorDto
            {
                Id = v.Id,
                InternalName = v.InternalName,
                IsActive = v.IsActive,
                ProductMappingCount = v.VendorProducts.Count,
                CreatedAt = v.CreatedAt,
                UpdatedAt = v.UpdatedAt
            })
            .ToListAsync(cancellationToken);

        return new PagedResult<AdminVendorDto>(items, safePage, safePageSize, totalCount);
    }

    public async Task<AdminCatalogResult<AdminVendorDto>> GetVendorByIdAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        var vendor = await _dbContext.Vendors
            .AsNoTracking()
            .Include(v => v.VendorProducts)
            .Where(v => v.Id == id)
            .Select(v => new AdminVendorDto
            {
                Id = v.Id,
                InternalName = v.InternalName,
                IsActive = v.IsActive,
                ProductMappingCount = v.VendorProducts.Count,
                CreatedAt = v.CreatedAt,
                UpdatedAt = v.UpdatedAt
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (vendor == null)
        {
            return AdminCatalogResult<AdminVendorDto>.NotFound($"Vendor with ID {id} was not found.");
        }

        return AdminCatalogResult<AdminVendorDto>.Ok(vendor);
    }

    public async Task<AdminCatalogResult<AdminVendorDto>> CreateVendorAsync(
        CreateVendorRequest request,
        CancellationToken cancellationToken = default)
    {
        var name = request.InternalName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return AdminCatalogResult<AdminVendorDto>.BadRequest("Vendor internal name is required.");
        }

        var exists = await _dbContext.Vendors
            .AnyAsync(v => v.InternalName.ToLower() == name.ToLower(), cancellationToken);

        if (exists)
        {
            return AdminCatalogResult<AdminVendorDto>.Conflict($"A vendor with name '{name}' already exists.");
        }

        var now = DateTime.UtcNow;
        var vendor = new Vendor
        {
            InternalName = name,
            IsActive = request.IsActive,
            CreatedAt = now,
            UpdatedAt = now
        };

        _dbContext.Vendors.Add(vendor);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var dto = new AdminVendorDto
        {
            Id = vendor.Id,
            InternalName = vendor.InternalName,
            IsActive = vendor.IsActive,
            ProductMappingCount = 0,
            CreatedAt = vendor.CreatedAt,
            UpdatedAt = vendor.UpdatedAt
        };

        return AdminCatalogResult<AdminVendorDto>.Ok(dto, "Vendor created successfully.");
    }

    public async Task<AdminCatalogResult<AdminVendorDto>> UpdateVendorAsync(
        int id,
        UpdateVendorRequest request,
        CancellationToken cancellationToken = default)
    {
        var vendor = await _dbContext.Vendors
            .Include(v => v.VendorProducts)
            .FirstOrDefaultAsync(v => v.Id == id, cancellationToken);

        if (vendor == null)
        {
            return AdminCatalogResult<AdminVendorDto>.NotFound($"Vendor with ID {id} was not found.");
        }

        var name = request.InternalName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return AdminCatalogResult<AdminVendorDto>.BadRequest("Vendor internal name is required.");
        }

        var exists = await _dbContext.Vendors
            .AnyAsync(v => v.InternalName.ToLower() == name.ToLower() && v.Id != id, cancellationToken);

        if (exists)
        {
            return AdminCatalogResult<AdminVendorDto>.Conflict($"A vendor with name '{name}' already exists.");
        }

        vendor.InternalName = name;
        vendor.IsActive = request.IsActive;
        vendor.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);

        var dto = new AdminVendorDto
        {
            Id = vendor.Id,
            InternalName = vendor.InternalName,
            IsActive = vendor.IsActive,
            ProductMappingCount = vendor.VendorProducts.Count,
            CreatedAt = vendor.CreatedAt,
            UpdatedAt = vendor.UpdatedAt
        };

        return AdminCatalogResult<AdminVendorDto>.Ok(dto, "Vendor updated successfully.");
    }

    public async Task<AdminCatalogResult<AdminVendorDto>> UpdateVendorStatusAsync(
        int id,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        var vendor = await _dbContext.Vendors
            .Include(v => v.VendorProducts)
            .FirstOrDefaultAsync(v => v.Id == id, cancellationToken);

        if (vendor == null)
        {
            return AdminCatalogResult<AdminVendorDto>.NotFound($"Vendor with ID {id} was not found.");
        }

        // Note: Disabling a vendor preserves its product mappings intact.
        vendor.IsActive = isActive;
        vendor.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);

        var dto = new AdminVendorDto
        {
            Id = vendor.Id,
            InternalName = vendor.InternalName,
            IsActive = vendor.IsActive,
            ProductMappingCount = vendor.VendorProducts.Count,
            CreatedAt = vendor.CreatedAt,
            UpdatedAt = vendor.UpdatedAt
        };

        return AdminCatalogResult<AdminVendorDto>.Ok(dto, $"Vendor status updated to {(isActive ? "active" : "inactive")}.");
    }

    // ── VendorProduct Mapping Operations ────────────────────────────────────────

    public async Task<PagedResult<AdminVendorProductDto>> GetVendorProductsAsync(
        AdminVendorProductFilterQuery query,
        CancellationToken cancellationToken = default)
    {
        var safePage = query.Page < 1 ? 1 : query.Page;
        var safePageSize = query.PageSize < 1 ? 20 : Math.Min(query.PageSize, 100);

        var dbQuery = _dbContext.VendorProducts
            .AsNoTracking()
            .Include(vp => vp.Vendor)
            .Include(vp => vp.Product)
            .AsQueryable();

        if (query.VendorId.HasValue)
        {
            dbQuery = dbQuery.Where(vp => vp.VendorId == query.VendorId.Value);
        }

        if (query.ProductId.HasValue)
        {
            dbQuery = dbQuery.Where(vp => vp.ProductId == query.ProductId.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.ProductSku))
        {
            var sku = query.ProductSku.Trim().ToLowerInvariant();
            dbQuery = dbQuery.Where(vp => vp.Product.InternalSku.ToLower() == sku);
        }

        if (query.IsAvailable.HasValue)
        {
            dbQuery = dbQuery.Where(vp => vp.IsAvailable == query.IsAvailable.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Currency))
        {
            var currency = query.Currency.Trim().ToLowerInvariant();
            dbQuery = dbQuery.Where(vp => vp.VendorCostCurrency.ToLower() == currency);
        }

        var totalCount = await dbQuery.CountAsync(cancellationToken);

        var items = await dbQuery
            .OrderBy(vp => vp.ProductId)
            .ThenBy(vp => vp.Priority)
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .Select(vp => new AdminVendorProductDto
            {
                Id = vp.Id,
                VendorId = vp.VendorId,
                VendorName = vp.Vendor.InternalName,
                VendorIsActive = vp.Vendor.IsActive,
                ProductId = vp.ProductId,
                ProductSku = vp.Product.InternalSku,
                ProductName = vp.Product.Name,
                ProductSellingPrice = vp.Product.SellingPrice,
                ProductSellingCurrency = vp.Product.SellingCurrency,
                ProductIsActive = vp.Product.IsActive,
                ExternalProductId = vp.ExternalProductId,
                VendorCost = vp.VendorCost,
                VendorCostCurrency = vp.VendorCostCurrency,
                IsAvailable = vp.IsAvailable,
                Priority = vp.Priority,
                CreatedAt = vp.CreatedAt,
                UpdatedAt = vp.UpdatedAt
            })
            .ToListAsync(cancellationToken);

        return new PagedResult<AdminVendorProductDto>(items, safePage, safePageSize, totalCount);
    }

    public async Task<AdminCatalogResult<AdminVendorProductDto>> GetVendorProductByIdAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        var mapping = await _dbContext.VendorProducts
            .AsNoTracking()
            .Include(vp => vp.Vendor)
            .Include(vp => vp.Product)
            .Where(vp => vp.Id == id)
            .Select(vp => new AdminVendorProductDto
            {
                Id = vp.Id,
                VendorId = vp.VendorId,
                VendorName = vp.Vendor.InternalName,
                VendorIsActive = vp.Vendor.IsActive,
                ProductId = vp.ProductId,
                ProductSku = vp.Product.InternalSku,
                ProductName = vp.Product.Name,
                ProductSellingPrice = vp.Product.SellingPrice,
                ProductSellingCurrency = vp.Product.SellingCurrency,
                ProductIsActive = vp.Product.IsActive,
                ExternalProductId = vp.ExternalProductId,
                VendorCost = vp.VendorCost,
                VendorCostCurrency = vp.VendorCostCurrency,
                IsAvailable = vp.IsAvailable,
                Priority = vp.Priority,
                CreatedAt = vp.CreatedAt,
                UpdatedAt = vp.UpdatedAt
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (mapping == null)
        {
            return AdminCatalogResult<AdminVendorProductDto>.NotFound($"Vendor product mapping with ID {id} was not found.");
        }

        return AdminCatalogResult<AdminVendorProductDto>.Ok(mapping);
    }

    public async Task<AdminCatalogResult<AdminVendorProductDto>> CreateVendorProductAsync(
        CreateVendorProductRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.VendorCost < 0)
        {
            return AdminCatalogResult<AdminVendorProductDto>.BadRequest("Vendor purchase cost cannot be negative.");
        }

        if (request.Priority < 0)
        {
            return AdminCatalogResult<AdminVendorProductDto>.BadRequest("Priority must be a non-negative integer.");
        }

        var extId = request.ExternalProductId.Trim();
        if (string.IsNullOrWhiteSpace(extId))
        {
            return AdminCatalogResult<AdminVendorProductDto>.BadRequest("External product ID is required.");
        }

        var currency = NormalizeAndValidateCurrency(request.VendorCostCurrency);
        if (currency == null)
        {
            return AdminCatalogResult<AdminVendorProductDto>.BadRequest(
                "Invalid currency code. Supported currencies include standard currency codes (e.g. 'PKR', 'USD', 'USDT').");
        }

        var vendor = await _dbContext.Vendors
            .FirstOrDefaultAsync(v => v.Id == request.VendorId, cancellationToken);
        if (vendor == null)
        {
            return AdminCatalogResult<AdminVendorProductDto>.BadRequest($"Vendor with ID {request.VendorId} does not exist.");
        }

        var product = await _dbContext.Products
            .FirstOrDefaultAsync(p => p.Id == request.ProductId, cancellationToken);
        if (product == null)
        {
            return AdminCatalogResult<AdminVendorProductDto>.BadRequest($"Product with ID {request.ProductId} does not exist.");
        }

        // Prevent duplicate VendorId + ProductId mappings per database unique constraint
        var duplicateExists = await _dbContext.VendorProducts
            .AnyAsync(vp => vp.VendorId == request.VendorId && vp.ProductId == request.ProductId, cancellationToken);
        if (duplicateExists)
        {
            return AdminCatalogResult<AdminVendorProductDto>.Conflict(
                $"A mapping for Vendor ID {request.VendorId} and Product ID {request.ProductId} already exists.");
        }

        var now = DateTime.UtcNow;
        var mapping = new VendorProduct
        {
            VendorId = vendor.Id,
            ProductId = product.Id,
            ExternalProductId = extId,
            VendorCost = request.VendorCost,
            VendorCostCurrency = currency,
            IsAvailable = request.IsAvailable,
            Priority = request.Priority,
            CreatedAt = now,
            UpdatedAt = now
        };

        _dbContext.VendorProducts.Add(mapping);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var dto = new AdminVendorProductDto
        {
            Id = mapping.Id,
            VendorId = vendor.Id,
            VendorName = vendor.InternalName,
            VendorIsActive = vendor.IsActive,
            ProductId = product.Id,
            ProductSku = product.InternalSku,
            ProductName = product.Name,
            ProductSellingPrice = product.SellingPrice,
            ProductSellingCurrency = product.SellingCurrency,
            ProductIsActive = product.IsActive,
            ExternalProductId = mapping.ExternalProductId,
            VendorCost = mapping.VendorCost,
            VendorCostCurrency = mapping.VendorCostCurrency,
            IsAvailable = mapping.IsAvailable,
            Priority = mapping.Priority,
            CreatedAt = mapping.CreatedAt,
            UpdatedAt = mapping.UpdatedAt
        };

        return AdminCatalogResult<AdminVendorProductDto>.Ok(dto, "Vendor product mapping created successfully.");
    }

    public async Task<AdminCatalogResult<AdminVendorProductDto>> UpdateVendorProductAsync(
        int id,
        UpdateVendorProductRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.VendorCost < 0)
        {
            return AdminCatalogResult<AdminVendorProductDto>.BadRequest("Vendor purchase cost cannot be negative.");
        }

        if (request.Priority < 0)
        {
            return AdminCatalogResult<AdminVendorProductDto>.BadRequest("Priority must be a non-negative integer.");
        }

        var extId = request.ExternalProductId.Trim();
        if (string.IsNullOrWhiteSpace(extId))
        {
            return AdminCatalogResult<AdminVendorProductDto>.BadRequest("External product ID is required.");
        }

        var currency = NormalizeAndValidateCurrency(request.VendorCostCurrency);
        if (currency == null)
        {
            return AdminCatalogResult<AdminVendorProductDto>.BadRequest(
                "Invalid currency code. Supported currencies include standard currency codes (e.g. 'PKR', 'USD', 'USDT').");
        }

        var mapping = await _dbContext.VendorProducts
            .Include(vp => vp.Vendor)
            .Include(vp => vp.Product)
            .FirstOrDefaultAsync(vp => vp.Id == id, cancellationToken);

        if (mapping == null)
        {
            return AdminCatalogResult<AdminVendorProductDto>.NotFound($"Vendor product mapping with ID {id} was not found.");
        }

        mapping.ExternalProductId = extId;
        mapping.VendorCost = request.VendorCost;
        mapping.VendorCostCurrency = currency;
        mapping.IsAvailable = request.IsAvailable;
        mapping.Priority = request.Priority;
        mapping.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);

        var dto = new AdminVendorProductDto
        {
            Id = mapping.Id,
            VendorId = mapping.VendorId,
            VendorName = mapping.Vendor.InternalName,
            VendorIsActive = mapping.Vendor.IsActive,
            ProductId = mapping.ProductId,
            ProductSku = mapping.Product.InternalSku,
            ProductName = mapping.Product.Name,
            ProductSellingPrice = mapping.Product.SellingPrice,
            ProductSellingCurrency = mapping.Product.SellingCurrency,
            ProductIsActive = mapping.Product.IsActive,
            ExternalProductId = mapping.ExternalProductId,
            VendorCost = mapping.VendorCost,
            VendorCostCurrency = mapping.VendorCostCurrency,
            IsAvailable = mapping.IsAvailable,
            Priority = mapping.Priority,
            CreatedAt = mapping.CreatedAt,
            UpdatedAt = mapping.UpdatedAt
        };

        return AdminCatalogResult<AdminVendorProductDto>.Ok(dto, "Vendor product mapping updated successfully.");
    }

    public async Task<AdminCatalogResult<AdminVendorProductDto>> UpdateVendorProductAvailabilityAsync(
        int id,
        bool isAvailable,
        CancellationToken cancellationToken = default)
    {
        var mapping = await _dbContext.VendorProducts
            .Include(vp => vp.Vendor)
            .Include(vp => vp.Product)
            .FirstOrDefaultAsync(vp => vp.Id == id, cancellationToken);

        if (mapping == null)
        {
            return AdminCatalogResult<AdminVendorProductDto>.NotFound($"Vendor product mapping with ID {id} was not found.");
        }

        mapping.IsAvailable = isAvailable;
        mapping.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);

        var dto = new AdminVendorProductDto
        {
            Id = mapping.Id,
            VendorId = mapping.VendorId,
            VendorName = mapping.Vendor.InternalName,
            VendorIsActive = mapping.Vendor.IsActive,
            ProductId = mapping.ProductId,
            ProductSku = mapping.Product.InternalSku,
            ProductName = mapping.Product.Name,
            ProductSellingPrice = mapping.Product.SellingPrice,
            ProductSellingCurrency = mapping.Product.SellingCurrency,
            ProductIsActive = mapping.Product.IsActive,
            ExternalProductId = mapping.ExternalProductId,
            VendorCost = mapping.VendorCost,
            VendorCostCurrency = mapping.VendorCostCurrency,
            IsAvailable = mapping.IsAvailable,
            Priority = mapping.Priority,
            CreatedAt = mapping.CreatedAt,
            UpdatedAt = mapping.UpdatedAt
        };

        return AdminCatalogResult<AdminVendorProductDto>.Ok(
            dto,
            $"Vendor product availability updated to {(isAvailable ? "available" : "unavailable")}.");
    }

    // ── Helper Methods ─────────────────────────────────────────────────────────

    private static string? NormalizeAndValidateCurrency(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var trimmed = input.Trim().ToUpperInvariant();
        if (trimmed.Length < 3 || trimmed.Length > 10)
        {
            return null;
        }

        // Must be alphanumeric characters (e.g. PKR, USD, USDT, EUR)
        if (!trimmed.All(char.IsLetterOrDigit))
        {
            return null;
        }

        return trimmed;
    }
}
