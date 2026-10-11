using Microsoft.AspNetCore.Mvc;
using WebApiTgBot.Common.Models;
using WebApiTgBot.Modules.Catalog.Models.DTOs;
using WebApiTgBot.Modules.Catalog.Services;

namespace WebApiTgBot.Modules.Catalog.Controllers;

/// <summary>
/// Public read-only API endpoints for browsing and searching products.
/// Only active products belonging to active categories are returned.
/// Vendor information is strictly excluded from all public responses.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class ProductsController : ControllerBase
{
    private readonly ICatalogService _catalogService;

    public ProductsController(ICatalogService catalogService)
    {
        _catalogService = catalogService;
    }

    /// <summary>
    /// Lists active products with optional category filtering, text search, and pagination.
    /// </summary>
    /// <param name="category">Optional category slug to filter products by (e.g. "ai-tools").</param>
    /// <param name="search">Optional search term matching product name, internal SKU, or description.</param>
    /// <param name="page">Page number (defaults to 1).</param>
    /// <param name="pageSize">Number of items per page (defaults to 20, max 100).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<ProductSummaryDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetProducts(
        [FromQuery] string? category = null,
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await _catalogService.GetActiveProductsAsync(category, search, page, pageSize, cancellationToken);
        return Ok(ApiResponse<PagedResult<ProductSummaryDto>>.Ok(result, "Products retrieved successfully."));
    }

    /// <summary>
    /// Retrieves public product details by internal SKU.
    /// Returns 404 Not Found if the product is unknown, inactive, or belongs to an inactive category.
    /// </summary>
    /// <param name="sku">Our internal product SKU (e.g. "CHATGPT_PLUS_1M").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("{sku}")]
    [ProducesResponseType(typeof(ApiResponse<ProductDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<ProductDetailDto>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProductBySku(string sku, CancellationToken cancellationToken)
    {
        var product = await _catalogService.GetProductBySkuAsync(sku, cancellationToken);
        if (product == null)
        {
            return NotFound(ApiResponse<ProductDetailDto>.Fail($"Product with SKU '{sku}' was not found."));
        }

        return Ok(ApiResponse<ProductDetailDto>.Ok(product, "Product details retrieved successfully."));
    }
}
