namespace WebApiTgBot.Modules.Identity.Models.DTOs;

/// <summary>
/// Safe profile information of an authenticated administrator.
/// Passwords, hash keys, and internal security stamps are strictly excluded.
/// </summary>
public class AdminUserDto
{
    public string Id { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? FullName { get; set; }
    public IList<string> Roles { get; set; } = new List<string>();
}
