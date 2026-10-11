namespace WebApiTgBot.Modules.Catalog.Models.DTOs;

/// <summary>
/// Detailed public view of a product by SKU.
/// Contains customer-facing product information.
/// Vendor information, vendor costs, and vendor mappings are strictly excluded.
/// </summary>
public class ProductDetailDto
{
    public int Id { get; set; }
    public string InternalSku { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int CategoryId { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public string CategorySlug { get; set; } = string.Empty;
    public decimal SellingPrice { get; set; }
    public string SellingCurrency { get; set; } = "PKR";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
