using Microsoft.AspNetCore.Identity;

namespace WebApiTgBot.Modules.Identity.Models;

/// <summary>
/// Central application role entity extending ASP.NET Core IdentityRole.
/// </summary>
public class ApplicationRole : IdentityRole
{
    public ApplicationRole() : base()
    {
    }

    public ApplicationRole(string roleName) : base(roleName)
    {
    }
}
