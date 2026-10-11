namespace WebApiTgBot.Modules.Catalog.Models;

/// <summary>
/// A product category (e.g. "AI Tools", "Digital Products", "SMM Services").
/// Categories are generic by design — new product types are added as new categories,
/// not as new tables.
/// </summary>
public class Category
{
    public int Id { get; set; }

    /// <summary>
    /// Display name shown to customers (e.g. "AI Tools").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// URL-friendly identifier (e.g. "ai-tools").
    /// Used in API filters and future frontend URLs.
    /// Must be unique and lowercase.
    /// </summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Optional customer-facing description.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Only active categories are visible to customers.
    /// Inactive categories hide all their products too.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation — one category has many products
    public ICollection<Product> Products { get; set; } = new List<Product>();
}
