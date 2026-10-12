using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using WebApiTgBot.Common.Models;
using WebApiTgBot.Modules.Catalog.Models.DTOs.Admin;
using WebApiTgBot.Modules.Catalog.Services;
using WebApiTgBot.Modules.Identity.Models;

namespace WebApiTgBot.Modules.Catalog.Controllers;

/// <summary>
/// Secured administrator endpoints for managing products.
/// Requires the Administrator role and enforces Antiforgery / CSRF protection on state-changing methods.
/// Permanent deletion of products is disallowed by design.
/// </summary>
[ApiController]
[Route("api/admin/products")]
[Authorize(Roles = AppRoles.Admin)]
public class AdminProductsController : ControllerBase
{
    private readonly IAdminCatalogService _adminCatalogService;

    public AdminProductsController(IAdminCatalogService adminCatalogService)
    {
        _adminCatalogService = adminCatalogService;
    }

    /// <summary>
    /// Lists products with administrative filtering (search, category, active state) and pagination.
    /// Allows viewing both active and inactive products.
    /// </summary>
    /// <param name="query">Filter and pagination query options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<AdminProductDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<AdminProductDto>>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<AdminProductDto>>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetProducts(
        [FromQuery] AdminProductFilterQuery query,
        CancellationToken cancellationToken = default)
    {
        var result = await _adminCatalogService.GetProductsAsync(query, cancellationToken);
        return Ok(ApiResponse<PagedResult<AdminProductDto>>.Ok(result, "Products retrieved successfully."));
    }

    /// <summary>
    /// Retrieves a single product by its identifier.
    /// </summary>
    /// <param name="id">Product ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(ApiResponse<AdminProductDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<AdminProductDto>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<AdminProductDto>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<AdminProductDto>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetProductById(
        int id,
        CancellationToken cancellationToken = default)
    {
        var result = await _adminCatalogService.GetProductByIdAsync(id, cancellationToken);
        if (!result.IsSuccess)
        {
            return NotFound(ApiResponse<AdminProductDto>.Fail(result.Message));
        }

        return Ok(ApiResponse<AdminProductDto>.Ok(result.Data!, "Product retrieved successfully."));
    }

    /// <summary>
    /// Creates a new product.
    /// Validates category existence, unique internal SKU, and non-negative pricing.
    /// </summary>
    /// <param name="request">Product creation payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<AdminProductDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<AdminProductDto>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<AdminProductDto>), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiResponse<AdminProductDto>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<AdminProductDto>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateProduct(
        [FromBody] CreateProductRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _adminCatalogService.CreateProductAsync(request, cancellationToken);

        if (result.Status == AdminCatalogResultStatus.Conflict)
        {
            return Conflict(ApiResponse<AdminProductDto>.Fail(result.Message));
        }

        if (result.Status == AdminCatalogResultStatus.BadRequest)
        {
            return BadRequest(ApiResponse<AdminProductDto>.Fail(result.Message));
        }

        return CreatedAtAction(
            nameof(GetProductById),
            new { id = result.Data!.Id },
            ApiResponse<AdminProductDto>.Ok(result.Data, result.Message));
    }

    /// <summary>
    /// Updates an existing product's details, category assignment, price, and status.
    /// The internal SKU remains immutable.
    /// </summary>
    /// <param name="id">Product ID.</param>
    /// <param name="request">Product update payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPut("{id:int}")]
    [ProducesResponseType(typeof(ApiResponse<AdminProductDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<AdminProductDto>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<AdminProductDto>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<AdminProductDto>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<AdminProductDto>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdateProduct(
        int id,
        [FromBody] UpdateProductRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _adminCatalogService.UpdateProductAsync(id, request, cancellationToken);

        if (result.Status == AdminCatalogResultStatus.NotFound)
        {
            return NotFound(ApiResponse<AdminProductDto>.Fail(result.Message));
        }

        if (result.Status == AdminCatalogResultStatus.BadRequest)
        {
            return BadRequest(ApiResponse<AdminProductDto>.Fail(result.Message));
        }

        return Ok(ApiResponse<AdminProductDto>.Ok(result.Data!, result.Message));
    }

    /// <summary>
    /// Enables or disables a product (soft status toggle).
    /// </summary>
    /// <param name="id">Product ID.</param>
    /// <param name="request">Status payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPatch("{id:int}/status")]
    [ProducesResponseType(typeof(ApiResponse<AdminProductDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<AdminProductDto>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<AdminProductDto>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<AdminProductDto>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<AdminProductDto>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdateProductStatus(
        int id,
        [FromBody] UpdateProductStatusRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _adminCatalogService.UpdateProductStatusAsync(id, request.IsActive, cancellationToken);

        if (result.Status == AdminCatalogResultStatus.NotFound)
        {
            return NotFound(ApiResponse<AdminProductDto>.Fail(result.Message));
        }

        return Ok(ApiResponse<AdminProductDto>.Ok(result.Data!, result.Message));
    }
}
