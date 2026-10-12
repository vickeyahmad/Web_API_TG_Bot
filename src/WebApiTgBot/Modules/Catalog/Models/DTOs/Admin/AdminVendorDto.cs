namespace WebApiTgBot.Modules.Catalog.Models.DTOs.Admin;

/// <summary>
/// Data transfer object representing a vendor (supplier) in administrative views.
/// Internal only — supplier details are never exposed to customers.
/// </summary>
public class AdminVendorDto
{
    public int Id { get; set; }
    public string InternalName { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public int ProductMappingCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
