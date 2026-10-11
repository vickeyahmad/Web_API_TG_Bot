using System.Security.Claims;
using WebApiTgBot.Modules.Identity.Models.DTOs;

namespace WebApiTgBot.Modules.Identity.Services;

/// <summary>
/// Service interface for administrator authentication and session management.
/// </summary>
public interface IAdminAuthService
{
    /// <summary>
    /// Authenticates an administrator with email and password, establishing an HTTP session.
    /// Strictly rejects non-admin users, inactive accounts, and locked-out users.
    /// </summary>
    Task<AdminLoginResult> LoginAsync(string email, string password, bool rememberMe, CancellationToken cancellationToken = default);

    /// <summary>
    /// Terminates the current administrator session.
    /// </summary>
    Task LogoutAsync();

    /// <summary>
    /// Retrieves current administrator details from the ClaimsPrincipal of an active session.
    /// Returns null if unauthenticated or not an administrator.
    /// </summary>
    Task<AdminUserDto?> GetCurrentUserAsync(ClaimsPrincipal principal);
}
