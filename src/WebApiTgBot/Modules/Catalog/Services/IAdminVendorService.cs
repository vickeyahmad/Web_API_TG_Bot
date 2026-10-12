using WebApiTgBot.Common.Models;
using WebApiTgBot.Modules.Catalog.Models.DTOs.Admin;

namespace WebApiTgBot.Modules.Catalog.Services;

/// <summary>
/// Business logic contract for administrative vendor management and vendor-product mappings.
/// Strictly enforces supplier isolation, unique constraints, referential integrity,
/// non-negative multi-currency pricing, and non-destructive management.
/// </summary>
public interface IAdminVendorService
{
    // Vendor operations
    Task<PagedResult<AdminVendorDto>> GetVendorsAsync(AdminVendorFilterQuery query, CancellationToken cancellationToken = default);
    Task<AdminCatalogResult<AdminVendorDto>> GetVendorByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<AdminCatalogResult<AdminVendorDto>> CreateVendorAsync(CreateVendorRequest request, CancellationToken cancellationToken = default);
    Task<AdminCatalogResult<AdminVendorDto>> UpdateVendorAsync(int id, UpdateVendorRequest request, CancellationToken cancellationToken = default);
    Task<AdminCatalogResult<AdminVendorDto>> UpdateVendorStatusAsync(int id, bool isActive, CancellationToken cancellationToken = default);

    // VendorProduct mapping operations
    Task<PagedResult<AdminVendorProductDto>> GetVendorProductsAsync(AdminVendorProductFilterQuery query, CancellationToken cancellationToken = default);
    Task<AdminCatalogResult<AdminVendorProductDto>> GetVendorProductByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<AdminCatalogResult<AdminVendorProductDto>> CreateVendorProductAsync(CreateVendorProductRequest request, CancellationToken cancellationToken = default);
    Task<AdminCatalogResult<AdminVendorProductDto>> UpdateVendorProductAsync(int id, UpdateVendorProductRequest request, CancellationToken cancellationToken = default);
    Task<AdminCatalogResult<AdminVendorProductDto>> UpdateVendorProductAvailabilityAsync(int id, bool isAvailable, CancellationToken cancellationToken = default);
}
