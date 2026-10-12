namespace WebApiTgBot.Modules.Catalog.Models.DTOs.Admin;

/// <summary>
/// Data transfer object representing a vendor-to-product mapping in administrative views.
/// Contains internal supplier cost, external product ID, availability, and routing priority.
/// INTERNAL ONLY — strictly forbidden from public customer-facing APIs.
/// </summary>
public class AdminVendorProductDto
{
    public int Id { get; set; }

    // Vendor details
    public int VendorId { get; set; }
    public string VendorName { get; set; } = string.Empty;
    public bool VendorIsActive { get; set; }

    // Product details
    public int ProductId { get; set; }
    public string ProductSku { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public decimal ProductSellingPrice { get; set; }
    public string ProductSellingCurrency { get; set; } = string.Empty;
    public bool ProductIsActive { get; set; }

    // Mapping attributes
    public string ExternalProductId { get; set; } = string.Empty;
    public decimal VendorCost { get; set; }
    public string VendorCostCurrency { get; set; } = string.Empty;
    public bool IsAvailable { get; set; }
    public int Priority { get; set; }

    // Audit timestamps
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
