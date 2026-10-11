using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using WebApiTgBot.Common.Models;
using WebApiTgBot.Modules.Identity.Models;
using WebApiTgBot.Modules.Identity.Models.DTOs;
using WebApiTgBot.Modules.Identity.Services;

namespace WebApiTgBot.Modules.Identity.Controllers;

/// <summary>
/// Secure administrator authentication and session management endpoints.
/// Strictly enforces administrator role authorization, lockout protection, rate limiting,
/// and Antiforgery / CSRF validation for state-changing operations.
/// </summary>
[ApiController]
[Route("api/admin/auth")]
public class AdminAuthController : ControllerBase
{
    private readonly IAdminAuthService _authService;

    public AdminAuthController(IAdminAuthService authService)
    {
        _authService = authService;
    }

    /// <summary>
    /// Issues an Antiforgery / CSRF request token in a protected, non-cacheable JSON response.
    /// Safe GET operation; exempt from CSRF token validation.
    /// The frontend (e.g. admin.gogaabyte.com) reads the token directly from the response JSON body
    /// and supplies it in the X-XSRF-TOKEN header on state-changing requests,
    /// avoiding reliance on client-side cookie reading across subdomains.
    /// </summary>
    [HttpGet("csrf")]
    [IgnoreAntiforgeryToken]
    [ProducesResponseType(typeof(ApiResponse<string>), StatusCodes.Status200OK)]
    public IActionResult GetCsrfToken([FromServices] IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);

        // Ensure token is never stored by intermediate proxies or browser caches
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        Response.Headers.Pragma = "no-cache";

        return Ok(ApiResponse<string>.Ok(tokens.RequestToken!, "Antiforgery token generated."));
    }

    /// <summary>
    /// Authenticates an administrator and establishes a secure HTTP-only session cookie.
    /// Rate limited to mitigate brute-force attacks.
    /// Exempt from CSRF validation because the user does not possess an active session yet.
    /// </summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    [EnableRateLimiting("AdminAuthLimiter")]
    [ProducesResponseType(typeof(ApiResponse<AdminUserDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<AdminUserDto>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<AdminUserDto>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<AdminUserDto>), StatusCodes.Status423Locked)]
    public async Task<IActionResult> Login([FromBody] AdminLoginRequest request, CancellationToken cancellationToken)
    {
        var result = await _authService.LoginAsync(
            request.Email,
            request.Password,
            request.RememberMe,
            cancellationToken);

        return result.Status switch
        {
            AdminAuthStatus.Success =>
                Ok(ApiResponse<AdminUserDto>.Ok(result.User!, result.Message)),

            AdminAuthStatus.LockedOut =>
                StatusCode(StatusCodes.Status423Locked, ApiResponse<AdminUserDto>.Fail(result.Message)),

            AdminAuthStatus.InactiveAccount or AdminAuthStatus.NotAnAdministrator =>
                StatusCode(StatusCodes.Status403Forbidden, ApiResponse<AdminUserDto>.Fail(result.Message)),

            _ =>
                Unauthorized(ApiResponse<AdminUserDto>.Fail(result.Message))
        };
    }

    /// <summary>
    /// Terminates the current administrator session.
    /// Requires active administrator authorization and valid CSRF token.
    /// </summary>
    [HttpPost("logout")]
    [Authorize(Roles = AppRoles.Admin)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Logout()
    {
        await _authService.LogoutAsync();
        return Ok(ApiResponse<object>.Ok(null!, "Administrator logged out successfully."));
    }

    /// <summary>
    /// Retrieves the profile of the currently authenticated administrator.
    /// Safe GET operation; requires active administrator authorization.
    /// </summary>
    [HttpGet("me")]
    [Authorize(Roles = AppRoles.Admin)]
    [ProducesResponseType(typeof(ApiResponse<AdminUserDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetCurrentUser()
    {
        var user = await _authService.GetCurrentUserAsync(User);
        if (user == null)
        {
            return Unauthorized(ApiResponse<AdminUserDto>.Fail("Unauthenticated or invalid administrator session."));
        }

        return Ok(ApiResponse<AdminUserDto>.Ok(user, "Current administrator profile retrieved."));
    }
}
