namespace WebApiTgBot.Modules.Identity.Models.DTOs;

/// <summary>
/// Safe customer profile data transfer object.
/// Excludes password hashes, security stamps, vendor data, and sensitive backend metadata.
/// </summary>
public class CustomerUserDto
{
    public string Id { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string? FullName { get; set; }

    public bool IsActive { get; set; }

    public bool EmailConfirmed { get; set; }

    public DateTime CreatedAt { get; set; }

    public IList<string> Roles { get; set; } = new List<string>();
}
