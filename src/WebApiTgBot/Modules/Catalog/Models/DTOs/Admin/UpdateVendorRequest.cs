using System.ComponentModel.DataAnnotations;

namespace WebApiTgBot.Modules.Catalog.Models.DTOs.Admin;

/// <summary>
/// Request payload for updating an existing vendor's information.
/// </summary>
public class UpdateVendorRequest
{
    [Required(ErrorMessage = "Vendor internal name is required.")]
    [StringLength(200, MinimumLength = 1, ErrorMessage = "Vendor internal name must be between 1 and 200 characters.")]
    public string InternalName { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
}
