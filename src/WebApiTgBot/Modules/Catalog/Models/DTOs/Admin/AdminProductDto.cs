namespace WebApiTgBot.Modules.Catalog.Models.DTOs.Admin;

/// <summary>
/// Data transfer object representing a product in administrative views.
/// Includes product details, category associations, pricing, active status, and audit timestamps.
/// Vendor mapping details remain decoupled and are not exposed here.
/// </summary>
public class AdminProductDto
{
    public int Id { get; set; }
    public string InternalSku { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int CategoryId { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public string CategorySlug { get; set; } = string.Empty;
    public bool CategoryIsActive { get; set; }
    public decimal SellingPrice { get; set; }
    public string SellingCurrency { get; set; } = "PKR";
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
