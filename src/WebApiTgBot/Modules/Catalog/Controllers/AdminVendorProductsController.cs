using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using WebApiTgBot.Common.Models;
using WebApiTgBot.Modules.Catalog.Models.DTOs.Admin;
using WebApiTgBot.Modules.Catalog.Services;
using WebApiTgBot.Modules.Identity.Models;

namespace WebApiTgBot.Modules.Catalog.Controllers;

/// <summary>
/// Secured administrator endpoints for managing vendor-to-product mappings.
/// Requires the Administrator role and enforces Antiforgery / CSRF protection on state-changing methods.
/// Mapping details (external product IDs, purchase costs, vendor identities) are strictly internal.
/// Permanent deletion of mappings is disallowed by design.
/// </summary>
[ApiController]
[Route("api/admin/vendor-products")]
[Authorize(Roles = AppRoles.Admin)]
public class AdminVendorProductsController : ControllerBase
{
    private readonly IAdminVendorService _adminVendorService;

    public AdminVendorProductsController(IAdminVendorService adminVendorService)
    {
        _adminVendorService = adminVendorService;
    }

    /// <summary>
    /// Lists vendor-to-product mappings with filtering (by vendor, product, SKU, availability, currency) and pagination.
    /// </summary>
    /// <param name="query">Filter and pagination query options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<AdminVendorProductDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<AdminVendorProductDto>>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<AdminVendorProductDto>>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetVendorProducts(
        [FromQuery] AdminVendorProductFilterQuery query,
        CancellationToken cancellationToken = default)
    {
        var result = await _adminVendorService.GetVendorProductsAsync(query, cancellationToken);
        return Ok(ApiResponse<PagedResult<AdminVendorProductDto>>.Ok(result, "Vendor product mappings retrieved successfully."));
    }

    /// <summary>
    /// Retrieves a single vendor-to-product mapping by its identifier.
    /// </summary>
    /// <param name="id">VendorProduct mapping ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(ApiResponse<AdminVendorProductDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<AdminVendorProductDto>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<AdminVendorProductDto>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<AdminVendorProductDto>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetVendorProductById(
        int id,
        CancellationToken cancellationToken = default)
    {
        var result = await _adminVendorService.GetVendorProductByIdAsync(id, cancellationToken);
        if (!result.IsSuccess)
        {
            return NotFound(ApiResponse<AdminVendorProductDto>.Fail(result.Message));
        }

        return Ok(ApiResponse<AdminVendorProductDto>.Ok(result.Data!, "Vendor product mapping retrieved successfully."));
    }

    /// <summary>
    /// Creates a new vendor-to-product mapping.
    /// Validates vendor existence, product existence, non-negative cost, multi-currency support,
    /// and prevents duplicate VendorId + ProductId mappings.
    /// </summary>
    /// <param name="request">Mapping creation payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<AdminVendorProductDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<AdminVendorProductDto>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<AdminVendorProductDto>), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiResponse<AdminVendorProductDto>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<AdminVendorProductDto>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateVendorProduct(
        [FromBody] CreateVendorProductRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _adminVendorService.CreateVendorProductAsync(request, cancellationToken);

        if (result.Status == AdminCatalogResultStatus.Conflict)
        {
            return Conflict(ApiResponse<AdminVendorProductDto>.Fail(result.Message));
        }

        if (result.Status == AdminCatalogResultStatus.BadRequest)
        {
            return BadRequest(ApiResponse<AdminVendorProductDto>.Fail(result.Message));
        }

        return CreatedAtAction(
            nameof(GetVendorProductById),
            new { id = result.Data!.Id },
            ApiResponse<AdminVendorProductDto>.Ok(result.Data, result.Message));
    }

    /// <summary>
    /// Updates an existing vendor-to-product mapping (cost, currency, external product ID, availability, priority).
    /// </summary>
    /// <param name="id">Mapping ID.</param>
    /// <param name="request">Mapping update payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPut("{id:int}")]
    [ProducesResponseType(typeof(ApiResponse<AdminVendorProductDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<AdminVendorProductDto>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<AdminVendorProductDto>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<AdminVendorProductDto>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<AdminVendorProductDto>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdateVendorProduct(
        int id,
        [FromBody] UpdateVendorProductRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _adminVendorService.UpdateVendorProductAsync(id, request, cancellationToken);

        if (result.Status == AdminCatalogResultStatus.NotFound)
        {
            return NotFound(ApiResponse<AdminVendorProductDto>.Fail(result.Message));
        }

        if (result.Status == AdminCatalogResultStatus.BadRequest)
        {
            return BadRequest(ApiResponse<AdminVendorProductDto>.Fail(result.Message));
        }

        return Ok(ApiResponse<AdminVendorProductDto>.Ok(result.Data!, result.Message));
    }

    /// <summary>
    /// Enables or disables availability of a vendor-to-product mapping.
    /// </summary>
    /// <param name="id">Mapping ID.</param>
    /// <param name="request">Availability payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPatch("{id:int}/availability")]
    [ProducesResponseType(typeof(ApiResponse<AdminVendorProductDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<AdminVendorProductDto>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<AdminVendorProductDto>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<AdminVendorProductDto>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<AdminVendorProductDto>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdateVendorProductAvailability(
        int id,
        [FromBody] UpdateVendorProductAvailabilityRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _adminVendorService.UpdateVendorProductAvailabilityAsync(id, request.IsAvailable, cancellationToken);

        if (result.Status == AdminCatalogResultStatus.NotFound)
        {
            return NotFound(ApiResponse<AdminVendorProductDto>.Fail(result.Message));
        }

        return Ok(ApiResponse<AdminVendorProductDto>.Ok(result.Data!, result.Message));
    }
}
