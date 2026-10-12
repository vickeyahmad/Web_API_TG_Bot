using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using WebApiTgBot.Modules.Identity.Models.DTOs;

namespace WebApiTgBot.Modules.Identity.Services;

/// <summary>
/// Contract for customer registration, authentication, and session management.
/// Strictly assigns the Customer role server-side, segregates customer sessions,
/// and prevents exposure of credentials.
/// </summary>
public interface ICustomerAuthService
{
    /// <summary>
    /// Registers a new customer account, enforcing email uniqueness, password complexity,
    /// and server-side Customer role assignment.
    /// </summary>
    Task<CustomerAuthResult> RegisterAsync(CustomerRegisterRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates customer credentials, checks account status and lockout, and issues
    /// a dedicated customer HTTP-only authentication session cookie.
    /// </summary>
    Task<CustomerAuthResult> LoginAsync(string email, string password, bool rememberMe, HttpContext httpContext, CancellationToken cancellationToken = default);

    /// <summary>
    /// Terminates the current customer session cookie.
    /// </summary>
    Task LogoutAsync(HttpContext httpContext);

    /// <summary>
    /// Resolves the authenticated customer from ClaimsPrincipal and returns a safe profile.
    /// </summary>
    Task<CustomerUserDto?> GetCurrentUserAsync(ClaimsPrincipal principal);
}
