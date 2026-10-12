using System.ComponentModel.DataAnnotations;

namespace WebApiTgBot.Modules.Catalog.Models.DTOs.Admin;

/// <summary>
/// Request payload for creating a new vendor-to-product mapping.
/// </summary>
public class CreateVendorProductRequest
{
    [Range(1, int.MaxValue, ErrorMessage = "A valid VendorId is required.")]
    public int VendorId { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "A valid ProductId is required.")]
    public int ProductId { get; set; }

    [Required(ErrorMessage = "External product ID is required.")]
    [StringLength(200, MinimumLength = 1, ErrorMessage = "External product ID must be between 1 and 200 characters.")]
    public string ExternalProductId { get; set; } = string.Empty;

    [Range(0, 1000000000, ErrorMessage = "Vendor purchase cost cannot be negative.")]
    public decimal VendorCost { get; set; }

    [Required(ErrorMessage = "Vendor cost currency is required.")]
    [StringLength(10, MinimumLength = 3, ErrorMessage = "Vendor cost currency must be an ISO currency code or symbol (e.g. 'PKR', 'USD', 'USDT').")]
    public string VendorCostCurrency { get; set; } = "PKR";

    public bool IsAvailable { get; set; } = true;

    [Range(0, 10000, ErrorMessage = "Priority must be a non-negative integer.")]
    public int Priority { get; set; } = 10;
}
