using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using WebApiTgBot.Common.Models;
using WebApiTgBot.Modules.Catalog.Models.DTOs.Admin;
using WebApiTgBot.Modules.Catalog.Services;
using WebApiTgBot.Modules.Identity.Models;

namespace WebApiTgBot.Modules.Catalog.Controllers;

/// <summary>
/// Secured administrator endpoints for managing product categories.
/// Requires the Administrator role and enforces Antiforgery / CSRF protection on state-changing methods.
/// Permanent deletion of categories is disallowed by design.
/// </summary>
[ApiController]
[Route("api/admin/categories")]
[Authorize(Roles = AppRoles.Admin)]
public class AdminCategoriesController : ControllerBase
{
    private readonly IAdminCatalogService _adminCatalogService;

    public AdminCategoriesController(IAdminCatalogService adminCatalogService)
    {
        _adminCatalogService = adminCatalogService;
    }

    /// <summary>
    /// Lists all categories (active and inactive) for administrative overview,
    /// with optional filtering by active state.
    /// </summary>
    /// <param name="isActive">Optional filter: null = all categories; true = active only; false = inactive only.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<AdminCategoryDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<AdminCategoryDto>>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<AdminCategoryDto>>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetCategories(
        [FromQuery] bool? isActive = null,
        CancellationToken cancellationToken = default)
    {
        var categories = await _adminCatalogService.GetCategoriesAsync(isActive, cancellationToken);
        return Ok(ApiResponse<IReadOnlyList<AdminCategoryDto>>.Ok(categories, "Categories retrieved successfully."));
    }

    /// <summary>
    /// Retrieves a single category by its identifier.
    /// </summary>
    /// <param name="id">Category ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(ApiResponse<AdminCategoryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<AdminCategoryDto>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<AdminCategoryDto>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<AdminCategoryDto>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetCategoryById(
        int id,
        CancellationToken cancellationToken = default)
    {
        var result = await _adminCatalogService.GetCategoryByIdAsync(id, cancellationToken);
        if (!result.IsSuccess)
        {
            return NotFound(ApiResponse<AdminCategoryDto>.Fail(result.Message));
        }

        return Ok(ApiResponse<AdminCategoryDto>.Ok(result.Data!, "Category retrieved successfully."));
    }

    /// <summary>
    /// Creates a new product category.
    /// Automatically validates unique slug constraints.
    /// </summary>
    /// <param name="request">Category creation payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<AdminCategoryDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<AdminCategoryDto>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<AdminCategoryDto>), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiResponse<AdminCategoryDto>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<AdminCategoryDto>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateCategory(
        [FromBody] CreateCategoryRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _adminCatalogService.CreateCategoryAsync(request, cancellationToken);

        if (result.Status == AdminCatalogResultStatus.Conflict)
        {
            return Conflict(ApiResponse<AdminCategoryDto>.Fail(result.Message));
        }

        if (result.Status == AdminCatalogResultStatus.BadRequest)
        {
            return BadRequest(ApiResponse<AdminCategoryDto>.Fail(result.Message));
        }

        return CreatedAtAction(
            nameof(GetCategoryById),
            new { id = result.Data!.Id },
            ApiResponse<AdminCategoryDto>.Ok(result.Data, result.Message));
    }

    /// <summary>
    /// Updates an existing category's name, description, and slug.
    /// Prevents duplicate slugs across different categories.
    /// </summary>
    /// <param name="id">Category ID.</param>
    /// <param name="request">Category update payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPut("{id:int}")]
    [ProducesResponseType(typeof(ApiResponse<AdminCategoryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<AdminCategoryDto>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<AdminCategoryDto>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<AdminCategoryDto>), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiResponse<AdminCategoryDto>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<AdminCategoryDto>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdateCategory(
        int id,
        [FromBody] UpdateCategoryRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _adminCatalogService.UpdateCategoryAsync(id, request, cancellationToken);

        if (result.Status == AdminCatalogResultStatus.NotFound)
        {
            return NotFound(ApiResponse<AdminCategoryDto>.Fail(result.Message));
        }

        if (result.Status == AdminCatalogResultStatus.Conflict)
        {
            return Conflict(ApiResponse<AdminCategoryDto>.Fail(result.Message));
        }

        if (result.Status == AdminCatalogResultStatus.BadRequest)
        {
            return BadRequest(ApiResponse<AdminCategoryDto>.Fail(result.Message));
        }

        return Ok(ApiResponse<AdminCategoryDto>.Ok(result.Data!, result.Message));
    }

    /// <summary>
    /// Enables or disables a category (soft status toggle).
    /// Disabling a category automatically hides all its products from the public catalog.
    /// </summary>
    /// <param name="id">Category ID.</param>
    /// <param name="request">Status payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPatch("{id:int}/status")]
    [ProducesResponseType(typeof(ApiResponse<AdminCategoryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<AdminCategoryDto>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<AdminCategoryDto>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<AdminCategoryDto>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<AdminCategoryDto>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdateCategoryStatus(
        int id,
        [FromBody] UpdateCategoryStatusRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _adminCatalogService.UpdateCategoryStatusAsync(id, request.IsActive, cancellationToken);

        if (result.Status == AdminCatalogResultStatus.NotFound)
        {
            return NotFound(ApiResponse<AdminCategoryDto>.Fail(result.Message));
        }

        return Ok(ApiResponse<AdminCategoryDto>.Ok(result.Data!, result.Message));
    }
}
