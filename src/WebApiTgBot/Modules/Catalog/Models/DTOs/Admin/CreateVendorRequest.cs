using System.ComponentModel.DataAnnotations;

namespace WebApiTgBot.Modules.Catalog.Models.DTOs.Admin;

/// <summary>
/// Request payload for creating a new supplier vendor.
/// </summary>
public class CreateVendorRequest
{
    [Required(ErrorMessage = "Vendor internal name is required.")]
    [StringLength(200, MinimumLength = 1, ErrorMessage = "Vendor internal name must be between 1 and 200 characters.")]
    public string InternalName { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
}
