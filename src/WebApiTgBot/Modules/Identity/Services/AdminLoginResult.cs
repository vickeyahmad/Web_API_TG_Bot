using WebApiTgBot.Modules.Identity.Models.DTOs;

namespace WebApiTgBot.Modules.Identity.Services;

/// <summary>
/// Status codes representing the outcome of an admin authentication attempt.
/// </summary>
public enum AdminAuthStatus
{
    Success,
    InvalidCredentials,
    LockedOut,
    InactiveAccount,
    NotAnAdministrator
}

/// <summary>
/// Result returned by the administrator authentication service.
/// </summary>
public class AdminLoginResult
{
    public bool Succeeded => Status == AdminAuthStatus.Success;
    public AdminAuthStatus Status { get; set; }
    public string Message { get; set; } = string.Empty;
    public AdminUserDto? User { get; set; }

    public static AdminLoginResult Ok(AdminUserDto user) =>
        new() { Status = AdminAuthStatus.Success, Message = "Authentication successful.", User = user };

    public static AdminLoginResult InvalidCredentials() =>
        new() { Status = AdminAuthStatus.InvalidCredentials, Message = "Invalid email or password." };

    public static AdminLoginResult LockedOut() =>
        new() { Status = AdminAuthStatus.LockedOut, Message = "Account is temporarily locked out due to multiple failed login attempts. Please try again later." };

    public static AdminLoginResult Inactive() =>
        new() { Status = AdminAuthStatus.InactiveAccount, Message = "This account has been deactivated." };

    public static AdminLoginResult NotAnAdministrator() =>
        new() { Status = AdminAuthStatus.NotAnAdministrator, Message = "Access denied. Administrator privileges required." };
}
