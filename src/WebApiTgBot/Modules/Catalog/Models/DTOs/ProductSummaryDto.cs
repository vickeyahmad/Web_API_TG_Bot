namespace WebApiTgBot.Modules.Catalog.Models.DTOs;

/// <summary>
/// Public summary of a product for catalog listings and search results.
/// Contains ONLY customer-safe fields.
/// Vendor information, vendor costs, and vendor mappings are strictly excluded.
/// </summary>
public class ProductSummaryDto
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
}
