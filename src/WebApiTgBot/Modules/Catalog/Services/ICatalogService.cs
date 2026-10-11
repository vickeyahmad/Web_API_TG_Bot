using WebApiTgBot.Common.Models;
using WebApiTgBot.Modules.Catalog.Models.DTOs;

namespace WebApiTgBot.Modules.Catalog.Services;

/// <summary>
/// Business logic contract for public catalog operations.
/// </summary>
public interface ICatalogService
{
    /// <summary>
    /// Retrieves all active product categories.
    /// </summary>
    Task<IReadOnlyList<CategoryDto>> GetActiveCategoriesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves paginated active products belonging to active categories,
    /// with optional filtering by category slug and search keyword.
    /// </summary>
    Task<PagedResult<ProductSummaryDto>> GetActiveProductsAsync(
        string? categorySlug = null,
        string? search = null,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves active product details by our internal SKU.
    /// Returns null if the product does not exist, is inactive, or belongs to an inactive category.
    /// </summary>
    Task<ProductDetailDto?> GetProductBySkuAsync(string sku, CancellationToken cancellationToken = default);
}
