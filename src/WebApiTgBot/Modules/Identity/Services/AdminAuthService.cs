using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using System.Security.Claims;
using WebApiTgBot.Modules.Identity.Models;
using WebApiTgBot.Modules.Identity.Models.DTOs;

namespace WebApiTgBot.Modules.Identity.Services;

/// <summary>
/// Service implementing administrator authentication logic.
/// Enforces administrator role verification, account active state, lockout protection,
/// and safe profile mapping without leaking sensitive credentials.
/// </summary>
public class AdminAuthService : IAdminAuthService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly ILogger<AdminAuthService> _logger;

    public AdminAuthService(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        ILogger<AdminAuthService> logger)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _logger = logger;
    }

    public async Task<AdminLoginResult> LoginAsync(string email, string password, bool rememberMe, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return AdminLoginResult.InvalidCredentials();
        }

        var normalizedEmail = email.Trim();
        var user = await _userManager.FindByEmailAsync(normalizedEmail);

        if (user == null)
        {
            _logger.LogWarning("Admin login failed: Account not found for {Email}", MaskEmail(normalizedEmail));
            return AdminLoginResult.InvalidCredentials();
        }

        if (!user.IsActive)
        {
            _logger.LogWarning("Admin login rejected: Account is inactive for user {UserId}", user.Id);
            return AdminLoginResult.Inactive();
        }

        // Strict role check: only users with the Admin role are allowed to authenticate via admin endpoints
        var isAdmin = await _userManager.IsInRoleAsync(user, AppRoles.Admin);
        if (!isAdmin)
        {
            _logger.LogWarning("Admin login rejected: User {UserId} does not possess Administrator privileges", user.Id);
            return AdminLoginResult.NotAnAdministrator();
        }

        // Attempt password sign-in with lockout tracking enabled
        var result = await _signInManager.PasswordSignInAsync(
            user,
            password,
            isPersistent: rememberMe,
            lockoutOnFailure: true);

        if (result.IsLockedOut)
        {
            _logger.LogWarning("Admin login locked out for user {UserId}", user.Id);
            return AdminLoginResult.LockedOut();
        }

        if (result.Succeeded)
        {
            _logger.LogInformation("Admin login succeeded for user {UserId}", user.Id);
            var roles = await _userManager.GetRolesAsync(user);

            var userDto = new AdminUserDto
            {
                Id = user.Id,
                Email = user.Email ?? string.Empty,
                FullName = user.FullName,
                Roles = roles
            };

            return AdminLoginResult.Ok(userDto);
        }

        _logger.LogWarning("Admin login failed: Invalid password for user {UserId}", user.Id);
        return AdminLoginResult.InvalidCredentials();
    }

    public async Task LogoutAsync()
    {
        await _signInManager.SignOutAsync();
        _logger.LogInformation("Admin session signed out successfully.");
    }

    public async Task<AdminUserDto?> GetCurrentUserAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var user = await _userManager.GetUserAsync(principal);
        if (user == null || !user.IsActive)
        {
            return null;
        }

        var isAdmin = await _userManager.IsInRoleAsync(user, AppRoles.Admin);
        if (!isAdmin)
        {
            return null;
        }

        var roles = await _userManager.GetRolesAsync(user);

        return new AdminUserDto
        {
            Id = user.Id,
            Email = user.Email ?? string.Empty,
            FullName = user.FullName,
            Roles = roles
        };
    }

    private static string MaskEmail(string email)
    {
        var atIndex = email.IndexOf('@');
        if (atIndex <= 1) return "***";
        return email[0] + "***" + email[atIndex..];
    }
}
