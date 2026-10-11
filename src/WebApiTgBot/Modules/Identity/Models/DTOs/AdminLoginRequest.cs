using System.ComponentModel.DataAnnotations;

namespace WebApiTgBot.Modules.Identity.Models.DTOs;

/// <summary>
/// Request payload for administrator authentication.
/// </summary>
public class AdminLoginRequest
{
    [Required(ErrorMessage = "Email is required.")]
    [EmailAddress(ErrorMessage = "Invalid email format.")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Password is required.")]
    public string Password { get; set; } = string.Empty;

    public bool RememberMe { get; set; } = false;
}
