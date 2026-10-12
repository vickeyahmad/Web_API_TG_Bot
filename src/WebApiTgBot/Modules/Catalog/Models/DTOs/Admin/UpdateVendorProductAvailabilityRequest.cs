using System.ComponentModel.DataAnnotations;

namespace WebApiTgBot.Modules.Catalog.Models.DTOs.Admin;

/// <summary>
/// Request payload for enabling or disabling availability of a specific vendor mapping.
/// </summary>
public class UpdateVendorProductAvailabilityRequest
{
    [Required]
    public bool IsAvailable { get; set; }
}
