using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
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
/// Secure customer authentication and session management endpoints.
/// Supports customer self-registration, credential login with rate limiting,
/// CSRF token generation, session logout, and safe profile retrieval.
/// Sessions are strictly segregated on AuthSchemes.CustomerScheme.
/// </summary>
[ApiController]
[Route("api/customer/auth")]
public class CustomerAuthController : ControllerBase
{
    private readonly ICustomerAuthService _authService;

    public CustomerAuthController(ICustomerAuthService authService)
    {
        _authService = authService;
    }

    /// <summary>
    /// Issues an Antiforgery / CSRF request token in a protected, non-cacheable JSON response.
    /// Safe GET operation; exempt from CSRF token validation.
    /// The customer frontend (e.g. tools.gogaabyte.com, smm.gogaabyte.com) reads this token
    /// and supplies it in the X-XSRF-TOKEN header on state-changing requests.
    /// If an active customer session exists, binds the token to the authenticated customer principal.
    /// </summary>
    [HttpGet("csrf")]
    [IgnoreAntiforgeryToken]
    [ProducesResponseType(typeof(ApiResponse<string>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCsrfToken([FromServices] IAntiforgery antiforgery)
    {
        var authResult = await HttpContext.AuthenticateAsync(AuthSchemes.CustomerScheme);
        if (authResult.Succeeded && authResult.Principal != null)
        {
            HttpContext.User = authResult.Principal;
        }

        var tokens = antiforgery.GetAndStoreTokens(HttpContext);

        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        Response.Headers.Pragma = "no-cache";

        return Ok(ApiResponse<string>.Ok(tokens.RequestToken!, "Antiforgery token generated."));
    }

    /// <summary>
    /// Registers a new customer account.
    /// Strictly assigns Customer role server-side; ignores any client role claims.
    /// Rate limited to mitigate spam and automated registration attacks.
    /// </summary>
    [HttpPost("register")]
    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    [EnableRateLimiting("CustomerAuthLimiter")]
    [ProducesResponseType(typeof(ApiResponse<CustomerUserDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<CustomerUserDto>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<CustomerUserDto>), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register([FromBody] CustomerRegisterRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            var errors = string.Join("; ", ModelState.Values
                .SelectMany(v => v.Errors)
                .Select(e => e.ErrorMessage));

            return BadRequest(ApiResponse<CustomerUserDto>.Fail($"Validation failed: {errors}"));
        }

        var result = await _authService.RegisterAsync(request, cancellationToken);

        return result.Status switch
        {
            CustomerAuthStatus.Success =>
                StatusCode(StatusCodes.Status201Created, ApiResponse<CustomerUserDto>.Ok(result.User!, result.Message)),

            CustomerAuthStatus.DuplicateEmail =>
                Conflict(ApiResponse<CustomerUserDto>.Fail(result.Message)),

            CustomerAuthStatus.ValidationError =>
                BadRequest(ApiResponse<CustomerUserDto>.Fail(result.Errors != null ? string.Join("; ", result.Errors) : result.Message)),

            _ =>
                BadRequest(ApiResponse<CustomerUserDto>.Fail(result.Message))
        };
    }

    /// <summary>
    /// Authenticates a customer and establishes a secure HTTP-only session cookie (.WebAPI.Customer.Auth).
    /// Rate limited and protected by account lockout.
    /// </summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    [EnableRateLimiting("CustomerAuthLimiter")]
    [ProducesResponseType(typeof(ApiResponse<CustomerUserDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<CustomerUserDto>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<CustomerUserDto>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<CustomerUserDto>), StatusCodes.Status423Locked)]
    public async Task<IActionResult> Login([FromBody] CustomerLoginRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return Unauthorized(ApiResponse<CustomerUserDto>.Fail("Invalid email or password."));
        }

        var result = await _authService.LoginAsync(
            request.Email,
            request.Password,
            request.RememberMe,
            HttpContext,
            cancellationToken);

        return result.Status switch
        {
            CustomerAuthStatus.Success =>
                Ok(ApiResponse<CustomerUserDto>.Ok(result.User!, result.Message)),

            CustomerAuthStatus.LockedOut =>
                StatusCode(StatusCodes.Status423Locked, ApiResponse<CustomerUserDto>.Fail(result.Message)),

            CustomerAuthStatus.InactiveAccount =>
                StatusCode(StatusCodes.Status403Forbidden, ApiResponse<CustomerUserDto>.Fail(result.Message)),

            _ =>
                Unauthorized(ApiResponse<CustomerUserDto>.Fail(result.Message))
        };
    }

    /// <summary>
    /// Terminates the current customer session cookie.
    /// Requires active customer authorization and valid CSRF token.
    /// </summary>
    [HttpPost("logout")]
    [Authorize(AuthenticationSchemes = AuthSchemes.CustomerScheme, Roles = AppRoles.Customer)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Logout()
    {
        await _authService.LogoutAsync(HttpContext);
        return Ok(ApiResponse<object>.Ok(null!, "Customer logged out successfully."));
    }

    /// <summary>
    /// Retrieves the profile of the currently authenticated customer.
    /// Requires active customer authorization on AuthSchemes.CustomerScheme.
    /// </summary>
    [HttpGet("me")]
    [Authorize(AuthenticationSchemes = AuthSchemes.CustomerScheme, Roles = AppRoles.Customer)]
    [ProducesResponseType(typeof(ApiResponse<CustomerUserDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetCurrentUser()
    {
        var user = await _authService.GetCurrentUserAsync(User);
        if (user == null)
        {
            return Unauthorized(ApiResponse<CustomerUserDto>.Fail("Unauthenticated or invalid customer session."));
        }

        return Ok(ApiResponse<CustomerUserDto>.Ok(user, "Current customer profile retrieved."));
    }
}
