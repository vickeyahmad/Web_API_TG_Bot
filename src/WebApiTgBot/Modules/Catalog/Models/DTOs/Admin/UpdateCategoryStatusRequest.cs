using System.ComponentModel.DataAnnotations;

namespace WebApiTgBot.Modules.Catalog.Models.DTOs.Admin;

/// <summary>
/// Request payload for enabling or disabling a category.
/// </summary>
public class UpdateCategoryStatusRequest
{
    [Required]
    public bool IsActive { get; set; }
}
