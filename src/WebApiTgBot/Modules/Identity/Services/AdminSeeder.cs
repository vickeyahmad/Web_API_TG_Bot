using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WebApiTgBot.Modules.Identity.Models;

namespace WebApiTgBot.Modules.Identity.Services;

/// <summary>
/// Seeds essential application roles and an initial administrator account.
/// Credentials are read exclusively from environment variables or dotnet user-secrets.
/// Passwords are never hardcoded and never logged.
///
/// Security safeguards:
/// 1. Never overwrites existing administrators.
/// 2. In production, requires explicit opt-in (AdminSeed:EnableInProduction=true) to prevent accidental execution.
/// </summary>
public class AdminSeeder : IAdminSeeder
{
    private readonly RoleManager<ApplicationRole> _roleManager;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<AdminSeeder> _logger;

    public AdminSeeder(
        RoleManager<ApplicationRole> roleManager,
        UserManager<ApplicationUser> userManager,
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<AdminSeeder> logger)
    {
        _roleManager = roleManager;
        _userManager = userManager;
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        // 1. Ensure core roles exist
        foreach (var roleName in new[] { AppRoles.Admin, AppRoles.Customer })
        {
            if (!await _roleManager.RoleExistsAsync(roleName))
            {
                var roleResult = await _roleManager.CreateAsync(new ApplicationRole(roleName));
                if (roleResult.Succeeded)
                {
                    _logger.LogInformation("Role '{RoleName}' created successfully.", roleName);
                }
                else
                {
                    _logger.LogError("Failed to create role '{RoleName}': {Errors}",
                        roleName, string.Join(", ", roleResult.Errors.Select(e => e.Description)));
                }
            }
        }

        // 2. Safeguard against overwriting: check if any administrator account already exists
        var existingAdmins = await _userManager.GetUsersInRoleAsync(AppRoles.Admin);
        if (existingAdmins.Count > 0)
        {
            _logger.LogDebug("Administrator account(s) already exist ({Count} found). Seeding skipped.", existingAdmins.Count);
            return;
        }

        // 3. Safeguard against unintentional production seeding:
        // In production, require explicit opt-in flag before provisioning an administrator
        if (_environment.IsProduction())
        {
            var allowInProd = _configuration.GetValue<bool>("AdminSeed:EnableInProduction", false) ||
                              string.Equals(_configuration["ADMIN_SEED_ENABLE_IN_PRODUCTION"], "true", StringComparison.OrdinalIgnoreCase);

            if (!allowInProd)
            {
                _logger.LogWarning("Production environment detected. Initial administrator seeding is disabled by default to prevent unintentional provisioning. Set 'AdminSeed:EnableInProduction=true' or 'ADMIN_SEED_ENABLE_IN_PRODUCTION=true' to allow.");
                return;
            }
        }

        // 4. Read initial admin credentials securely from configuration/secrets
        var adminEmail = _configuration["AdminSeed:Email"] ?? _configuration["ADMIN_SEED_EMAIL"];
        var adminPassword = _configuration["AdminSeed:Password"] ?? _configuration["ADMIN_SEED_PASSWORD"];

        if (string.IsNullOrWhiteSpace(adminEmail) || string.IsNullOrWhiteSpace(adminPassword))
        {
            _logger.LogInformation("No administrator account found. To seed the initial admin, configure 'AdminSeed:Email' and 'AdminSeed:Password' via dotnet user-secrets or environment variables.");
            return;
        }

        // 5. Create the initial administrator user
        var newAdmin = new ApplicationUser
        {
            UserName = adminEmail.Trim(),
            Email = adminEmail.Trim(),
            FullName = "System Administrator",
            EmailConfirmed = true,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var createResult = await _userManager.CreateAsync(newAdmin, adminPassword);
        if (createResult.Succeeded)
        {
            await _userManager.AddToRoleAsync(newAdmin, AppRoles.Admin);
            _logger.LogInformation("Initial administrator account created for '{Email}'.", adminEmail.Trim());
        }
        else
        {
            _logger.LogError("Failed to create initial administrator account: {Errors}",
                string.Join(", ", createResult.Errors.Select(e => e.Description)));
        }
    }
}
