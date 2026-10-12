namespace WebApiTgBot.Modules.Identity.Models;

/// <summary>
/// Constants for authentication schemes used across the platform.
/// Strictly segregates administrator sessions from customer sessions at the cookie and handler level.
/// </summary>
public static class AuthSchemes
{
    /// <summary>
    /// Authentication scheme for administrators (uses IdentityConstants.ApplicationScheme).
    /// Cookie name: .WebAPI.Admin.Auth
    /// </summary>
    public const string AdminScheme = "Identity.Application";

    /// <summary>
    /// Dedicated authentication scheme for customers.
    /// Cookie name: .WebAPI.Customer.Auth
    /// </summary>
    public const string CustomerScheme = "CustomerCookie";
}
