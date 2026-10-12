using System.ComponentModel.DataAnnotations;

namespace WebApiTgBot.Modules.Identity.Models.DTOs;

/// <summary>
/// Data transfer object for customer registration requests.
/// Strict validation enforces valid email formatting and minimum password security.
/// Roles are deliberately excluded to prevent client-side privilege escalation.
/// </summary>
public class CustomerRegisterRequest
{
    [Required(ErrorMessage = "Email is required.")]
    [EmailAddress(ErrorMessage = "Invalid email address format.")]
    [MaxLength(256, ErrorMessage = "Email cannot exceed 256 characters.")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Password is required.")]
    [MinLength(8, ErrorMessage = "Password must be at least 8 characters long.")]
    [MaxLength(100, ErrorMessage = "Password cannot exceed 100 characters.")]
    public string Password { get; set; } = string.Empty;

    [MaxLength(150, ErrorMessage = "Full name cannot exceed 150 characters.")]
    public string? FullName { get; set; }
}
