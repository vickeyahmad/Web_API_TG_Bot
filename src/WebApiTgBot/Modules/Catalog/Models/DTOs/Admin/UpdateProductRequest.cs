using System.ComponentModel.DataAnnotations;

namespace WebApiTgBot.Modules.Catalog.Models.DTOs.Admin;

/// <summary>
/// Request payload for updating an existing product's details.
/// Note: InternalSku is deliberately excluded to preserve SKU immutability after creation.
/// </summary>
public class UpdateProductRequest
{
    [Required(ErrorMessage = "Product name is required.")]
    [StringLength(300, MinimumLength = 1, ErrorMessage = "Product name must be between 1 and 300 characters.")]
    public string Name { get; set; } = string.Empty;

    [MaxLength(5000, ErrorMessage = "Description cannot exceed 5000 characters.")]
    public string? Description { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "A valid CategoryId is required.")]
    public int CategoryId { get; set; }

    [Range(0, 1000000000, ErrorMessage = "Selling price must not be negative.")]
    public decimal SellingPrice { get; set; }

    [StringLength(10, MinimumLength = 3, ErrorMessage = "Selling currency must be an ISO currency code (e.g. 'PKR').")]
    public string SellingCurrency { get; set; } = "PKR";

    public bool IsActive { get; set; } = true;
}
