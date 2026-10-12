using System.ComponentModel.DataAnnotations;

namespace WebApiTgBot.Modules.Catalog.Models.DTOs.Admin;

/// <summary>
/// Request payload for creating a new product category.
/// </summary>
public class CreateCategoryRequest
{
    [Required(ErrorMessage = "Category name is required.")]
    [StringLength(200, MinimumLength = 1, ErrorMessage = "Category name must be between 1 and 200 characters.")]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "Category slug is required.")]
    [StringLength(200, MinimumLength = 1, ErrorMessage = "Category slug must be between 1 and 200 characters.")]
    [RegularExpression(@"^[a-z0-9]+(?:-[a-z0-9]+)*$", ErrorMessage = "Slug must be lowercase alphanumeric words separated by hyphens (e.g. 'ai-tools').")]
    public string Slug { get; set; } = string.Empty;

    [MaxLength(2000, ErrorMessage = "Description cannot exceed 2000 characters.")]
    public string? Description { get; set; }

    public bool IsActive { get; set; } = true;
}
