namespace WebApiTgBot.Modules.Catalog.Models.DTOs;

/// <summary>
/// Public representation of a product category.
/// Contains only customer-safe information.
/// </summary>
public class CategoryDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? Description { get; set; }
}
