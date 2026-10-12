using WebApiTgBot.Common.Models;
using WebApiTgBot.Modules.Catalog.Models.DTOs.Admin;

namespace WebApiTgBot.Modules.Catalog.Services;

/// <summary>
/// Business logic contract for administrative category and product management.
/// Enforces uniqueness constraints, category validation, audit timestamps, and non-destructive management.
/// </summary>
public interface IAdminCatalogService
{
    // Category management
    Task<IReadOnlyList<AdminCategoryDto>> GetCategoriesAsync(bool? isActive = null, CancellationToken cancellationToken = default);
    Task<AdminCatalogResult<AdminCategoryDto>> GetCategoryByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<AdminCatalogResult<AdminCategoryDto>> CreateCategoryAsync(CreateCategoryRequest request, CancellationToken cancellationToken = default);
    Task<AdminCatalogResult<AdminCategoryDto>> UpdateCategoryAsync(int id, UpdateCategoryRequest request, CancellationToken cancellationToken = default);
    Task<AdminCatalogResult<AdminCategoryDto>> UpdateCategoryStatusAsync(int id, bool isActive, CancellationToken cancellationToken = default);

    // Product management
    Task<PagedResult<AdminProductDto>> GetProductsAsync(AdminProductFilterQuery query, CancellationToken cancellationToken = default);
    Task<AdminCatalogResult<AdminProductDto>> GetProductByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<AdminCatalogResult<AdminProductDto>> CreateProductAsync(CreateProductRequest request, CancellationToken cancellationToken = default);
    Task<AdminCatalogResult<AdminProductDto>> UpdateProductAsync(int id, UpdateProductRequest request, CancellationToken cancellationToken = default);
    Task<AdminCatalogResult<AdminProductDto>> UpdateProductStatusAsync(int id, bool isActive, CancellationToken cancellationToken = default);
}
