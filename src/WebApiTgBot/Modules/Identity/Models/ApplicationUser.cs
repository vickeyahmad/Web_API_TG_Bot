using Microsoft.AspNetCore.Identity;

namespace WebApiTgBot.Modules.Identity.Models;

/// <summary>
/// Central application user entity extending ASP.NET Core IdentityUser.
/// Serves as the identity foundation for both administrators and future customers.
/// </summary>
public class ApplicationUser : IdentityUser
{
    /// <summary>Optional display name or full name of the user.</summary>
    public string? FullName { get; set; }

    /// <summary>
    /// Whether this user account is active.
    /// Inactive accounts are blocked from logging in.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
