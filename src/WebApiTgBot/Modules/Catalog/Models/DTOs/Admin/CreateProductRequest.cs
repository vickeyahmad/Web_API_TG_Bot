using System.ComponentModel.DataAnnotations;

namespace WebApiTgBot.Modules.Catalog.Models.DTOs.Admin;

/// <summary>
/// Request payload for creating a new product.
/// </summary>
public class CreateProductRequest
{
    [Required(ErrorMessage = "Internal SKU is required.")]
    [StringLength(100, MinimumLength = 2, ErrorMessage = "Internal SKU must be between 2 and 100 characters.")]
    [RegularExpression(@"^[A-Z0-9_]+$", ErrorMessage = "Internal SKU must contain only uppercase letters, numbers, and underscores (e.g. 'CHATGPT_PLUS_1M').")]
    public string InternalSku { get; set; } = string.Empty;

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
