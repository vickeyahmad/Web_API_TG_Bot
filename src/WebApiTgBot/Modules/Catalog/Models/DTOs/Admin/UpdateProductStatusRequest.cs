using System.ComponentModel.DataAnnotations;

namespace WebApiTgBot.Modules.Catalog.Models.DTOs.Admin;

/// <summary>
/// Request payload for enabling or disabling a product.
/// </summary>
public class UpdateProductStatusRequest
{
    [Required]
    public bool IsActive { get; set; }
}
