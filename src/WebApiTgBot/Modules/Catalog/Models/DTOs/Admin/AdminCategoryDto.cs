namespace WebApiTgBot.Modules.Catalog.Models.DTOs.Admin;

/// <summary>
/// Data transfer object representing a category in administrative views.
/// Includes administrative metadata such as active status, timestamps, and total product count.
/// </summary>
public class AdminCategoryDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; }
    public int ProductCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
