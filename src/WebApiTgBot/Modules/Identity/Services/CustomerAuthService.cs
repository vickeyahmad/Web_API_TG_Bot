using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using System.Security.Claims;
using WebApiTgBot.Modules.Identity.Models;
using WebApiTgBot.Modules.Identity.Models.DTOs;

namespace WebApiTgBot.Modules.Identity.Services;

/// <summary>
/// Service implementing customer registration, authentication, and session logic.
/// Enforces server-side Customer role assignment, lockout protection, email normalization,
/// account active state validation, and complete segregation from administrator sessions.
/// </summary>
public class CustomerAuthService : ICustomerAuthService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly RoleManager<ApplicationRole> _roleManager;
    private readonly ILogger<CustomerAuthService> _logger;

    public CustomerAuthService(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        RoleManager<ApplicationRole> roleManager,
        ILogger<CustomerAuthService> logger)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _roleManager = roleManager;
        _logger = logger;
    }

    public async Task<CustomerAuthResult> RegisterAsync(CustomerRegisterRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            return CustomerAuthResult.ValidationError(new[] { "Email and password are required." });
        }

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();

        // 1. Prevent duplicate email registrations
        var existingUser = await _userManager.FindByEmailAsync(normalizedEmail);
        if (existingUser != null)
        {
            _logger.LogWarning("Customer registration rejected: Duplicate email {Email}", MaskEmail(normalizedEmail));
            return CustomerAuthResult.DuplicateEmail("An account with this email address already exists.");
        }

        // 2. Ensure the Customer role exists in the database
        if (!await _roleManager.RoleExistsAsync(AppRoles.Customer))
        {
            await _roleManager.CreateAsync(new ApplicationRole(AppRoles.Customer));
        }

        // 3. Create the customer user with normalized email
        var user = new ApplicationUser
        {
            UserName = normalizedEmail,
            Email = normalizedEmail,
            FullName = string.IsNullOrWhiteSpace(request.FullName) ? null : request.FullName.Trim(),
            IsActive = true,
            EmailConfirmed = false, // Email verification will be integrated in future milestone
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var createResult = await _userManager.CreateAsync(user, request.Password);
        if (!createResult.Succeeded)
        {
            _logger.LogWarning("Customer registration failed password or validation checks for {Email}: {Errors}",
                MaskEmail(normalizedEmail),
                string.Join(", ", createResult.Errors.Select(e => e.Description)));

            return CustomerAuthResult.ValidationError(
                createResult.Errors.Select(e => e.Description),
                "Registration failed due to validation errors.");
        }

        // 4. Server-side role assignment: STRICTLY assign Customer role only
        // Customers can NEVER choose their own roles or elevate to Admin.
        var roleResult = await _userManager.AddToRoleAsync(user, AppRoles.Customer);
        if (!roleResult.Succeeded)
        {
            _logger.LogError("Failed to assign Customer role to user {UserId}: {Errors}",
                user.Id,
                string.Join(", ", roleResult.Errors.Select(e => e.Description)));

            return CustomerAuthResult.ValidationError(
                roleResult.Errors.Select(e => e.Description),
                "Failed to assign customer role.");
        }

        _logger.LogInformation("Customer registered successfully with UserId {UserId}", user.Id);

        var roles = await _userManager.GetRolesAsync(user);
        var userDto = new CustomerUserDto
        {
            Id = user.Id,
            Email = user.Email ?? string.Empty,
            FullName = user.FullName,
            IsActive = user.IsActive,
            EmailConfirmed = user.EmailConfirmed,
            CreatedAt = user.CreatedAt,
            Roles = roles
        };

        return CustomerAuthResult.Ok(userDto, "Customer account created successfully.");
    }

    public async Task<CustomerAuthResult> LoginAsync(
        string email,
        string password,
        bool rememberMe,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return CustomerAuthResult.InvalidCredentials();
        }

        var normalizedEmail = email.Trim().ToLowerInvariant();
        var user = await _userManager.FindByEmailAsync(normalizedEmail);

        if (user == null)
        {
            _logger.LogWarning("Customer login failed: Account not found for {Email}", MaskEmail(normalizedEmail));
            return CustomerAuthResult.InvalidCredentials();
        }

        // Strict active account check: disabled customers cannot log in
        if (!user.IsActive)
        {
            _logger.LogWarning("Customer login rejected: Account is inactive for user {UserId}", user.Id);
            return CustomerAuthResult.Inactive();
        }

        // Role verification: user must have the Customer role
        var isCustomer = await _userManager.IsInRoleAsync(user, AppRoles.Customer);
        if (!isCustomer)
        {
            _logger.LogWarning("Customer login rejected: User {UserId} is not a customer", user.Id);
            // Return generic invalid credentials to avoid revealing user existence
            return CustomerAuthResult.NotACustomer();
        }

        // Validate password with lockout tracking enabled
        var signInResult = await _signInManager.CheckPasswordSignInAsync(
            user,
            password,
            lockoutOnFailure: true);

        if (signInResult.IsLockedOut)
        {
            _logger.LogWarning("Customer account locked out for user {UserId}", user.Id);
            return CustomerAuthResult.LockedOut();
        }

        if (!signInResult.Succeeded)
        {
            _logger.LogWarning("Customer login failed: Invalid password for user {UserId}", user.Id);
            return CustomerAuthResult.InvalidCredentials();
        }

        // Issue dedicated customer session cookie on AuthSchemes.CustomerScheme
        var principal = await _signInManager.CreateUserPrincipalAsync(user);
        var authProperties = new AuthenticationProperties
        {
            IsPersistent = rememberMe,
            ExpiresUtc = rememberMe
                ? DateTimeOffset.UtcNow.AddDays(30)
                : DateTimeOffset.UtcNow.AddHours(24)
        };

        await httpContext.SignInAsync(AuthSchemes.CustomerScheme, principal, authProperties);

        _logger.LogInformation("Customer login succeeded for user {UserId}", user.Id);

        var roles = await _userManager.GetRolesAsync(user);
        var userDto = new CustomerUserDto
        {
            Id = user.Id,
            Email = user.Email ?? string.Empty,
            FullName = user.FullName,
            IsActive = user.IsActive,
            EmailConfirmed = user.EmailConfirmed,
            CreatedAt = user.CreatedAt,
            Roles = roles
        };

        return CustomerAuthResult.Ok(userDto, "Customer logged in successfully.");
    }

    public async Task LogoutAsync(HttpContext httpContext)
    {
        await httpContext.SignOutAsync(AuthSchemes.CustomerScheme);
        _logger.LogInformation("Customer session signed out successfully.");
    }

    public async Task<CustomerUserDto?> GetCurrentUserAsync(ClaimsPrincipal principal)
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

        var isCustomer = await _userManager.IsInRoleAsync(user, AppRoles.Customer);
        if (!isCustomer)
        {
            return null;
        }

        var roles = await _userManager.GetRolesAsync(user);
        return new CustomerUserDto
        {
            Id = user.Id,
            Email = user.Email ?? string.Empty,
            FullName = user.FullName,
            IsActive = user.IsActive,
            EmailConfirmed = user.EmailConfirmed,
            CreatedAt = user.CreatedAt,
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
