namespace WebApiTgBot.Modules.Identity.Services;

/// <summary>
/// Service interface for initial roles and administrator seeding.
/// </summary>
public interface IAdminSeeder
{
    /// <summary>
    /// Seeds core roles (Admin, Customer) and creates an initial administrator
    /// if none exists and credentials are provided via secure configuration.
    /// </summary>
    Task SeedAsync(CancellationToken cancellationToken = default);
}
