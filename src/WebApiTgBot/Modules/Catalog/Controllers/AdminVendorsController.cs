using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using WebApiTgBot.Common.Models;
using WebApiTgBot.Modules.Catalog.Models.DTOs.Admin;
using WebApiTgBot.Modules.Catalog.Services;
using WebApiTgBot.Modules.Identity.Models;

namespace WebApiTgBot.Modules.Catalog.Controllers;

/// <summary>
/// Secured administrator endpoints for managing suppliers (vendors).
/// Requires the Administrator role and enforces Antiforgery / CSRF protection on state-changing methods.
/// Vendor identities are strictly internal and permanently hidden from customers.
/// Permanent deletion of vendors is disallowed by design.
/// </summary>
[ApiController]
[Route("api/admin/vendors")]
[Authorize(Roles = AppRoles.Admin)]
public class AdminVendorsController : ControllerBase
{
    private readonly IAdminVendorService _adminVendorService;

    public AdminVendorsController(IAdminVendorService adminVendorService)
    {
        _adminVendorService = adminVendorService;
    }

    /// <summary>
    /// Lists vendors with administrative search and pagination.
    /// Supports viewing both active and inactive vendors.
    /// </summary>
    /// <param name="query">Filter and pagination query options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<AdminVendorDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<AdminVendorDto>>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<AdminVendorDto>>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetVendors(
        [FromQuery] AdminVendorFilterQuery query,
        CancellationToken cancellationToken = default)
    {
        var result = await _adminVendorService.GetVendorsAsync(query, cancellationToken);
        return Ok(ApiResponse<PagedResult<AdminVendorDto>>.Ok(result, "Vendors retrieved successfully."));
    }

    /// <summary>
    /// Retrieves a single vendor by its identifier.
    /// </summary>
    /// <param name="id">Vendor ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(ApiResponse<AdminVendorDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<AdminVendorDto>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<AdminVendorDto>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<AdminVendorDto>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetVendorById(
        int id,
        CancellationToken cancellationToken = default)
    {
        var result = await _adminVendorService.GetVendorByIdAsync(id, cancellationToken);
        if (!result.IsSuccess)
        {
            return NotFound(ApiResponse<AdminVendorDto>.Fail(result.Message));
        }

        return Ok(ApiResponse<AdminVendorDto>.Ok(result.Data!, "Vendor retrieved successfully."));
    }

    /// <summary>
    /// Creates a new vendor.
    /// Validates unique internal vendor name.
    /// </summary>
    /// <param name="request">Vendor creation payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<AdminVendorDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<AdminVendorDto>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<AdminVendorDto>), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiResponse<AdminVendorDto>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<AdminVendorDto>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateVendor(
        [FromBody] CreateVendorRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _adminVendorService.CreateVendorAsync(request, cancellationToken);

        if (result.Status == AdminCatalogResultStatus.Conflict)
        {
            return Conflict(ApiResponse<AdminVendorDto>.Fail(result.Message));
        }

        if (result.Status == AdminCatalogResultStatus.BadRequest)
        {
            return BadRequest(ApiResponse<AdminVendorDto>.Fail(result.Message));
        }

        return CreatedAtAction(
            nameof(GetVendorById),
            new { id = result.Data!.Id },
            ApiResponse<AdminVendorDto>.Ok(result.Data, result.Message));
    }

    /// <summary>
    /// Updates an existing vendor's internal information.
    /// Prevents duplicate names across different vendors.
    /// </summary>
    /// <param name="id">Vendor ID.</param>
    /// <param name="request">Vendor update payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPut("{id:int}")]
    [ProducesResponseType(typeof(ApiResponse<AdminVendorDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<AdminVendorDto>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<AdminVendorDto>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<AdminVendorDto>), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiResponse<AdminVendorDto>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<AdminVendorDto>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdateVendor(
        int id,
        [FromBody] UpdateVendorRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _adminVendorService.UpdateVendorAsync(id, request, cancellationToken);

        if (result.Status == AdminCatalogResultStatus.NotFound)
        {
            return NotFound(ApiResponse<AdminVendorDto>.Fail(result.Message));
        }

        if (result.Status == AdminCatalogResultStatus.Conflict)
        {
            return Conflict(ApiResponse<AdminVendorDto>.Fail(result.Message));
        }

        if (result.Status == AdminCatalogResultStatus.BadRequest)
        {
            return BadRequest(ApiResponse<AdminVendorDto>.Fail(result.Message));
        }

        return Ok(ApiResponse<AdminVendorDto>.Ok(result.Data!, result.Message));
    }

    /// <summary>
    /// Enables or disables a vendor (soft status toggle).
    /// Preserves all existing product mappings when disabled.
    /// </summary>
    /// <param name="id">Vendor ID.</param>
    /// <param name="request">Status payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPatch("{id:int}/status")]
    [ProducesResponseType(typeof(ApiResponse<AdminVendorDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<AdminVendorDto>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<AdminVendorDto>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<AdminVendorDto>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<AdminVendorDto>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdateVendorStatus(
        int id,
        [FromBody] UpdateVendorStatusRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _adminVendorService.UpdateVendorStatusAsync(id, request.IsActive, cancellationToken);

        if (result.Status == AdminCatalogResultStatus.NotFound)
        {
            return NotFound(ApiResponse<AdminVendorDto>.Fail(result.Message));
        }

        return Ok(ApiResponse<AdminVendorDto>.Ok(result.Data!, result.Message));
    }
}
