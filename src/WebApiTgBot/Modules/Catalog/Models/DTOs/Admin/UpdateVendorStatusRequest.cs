using System.ComponentModel.DataAnnotations;

namespace WebApiTgBot.Modules.Catalog.Models.DTOs.Admin;

/// <summary>
/// Request payload for enabling or disabling a vendor.
/// </summary>
public class UpdateVendorStatusRequest
{
    [Required]
    public bool IsActive { get; set; }
}
