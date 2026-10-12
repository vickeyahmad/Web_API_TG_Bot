namespace WebApiTgBot.Modules.Orders.Models;

/// <summary>
/// Sales channel through which the order originated.
/// All channels share the single central order system.
/// </summary>
public enum SalesChannel
{
    /// <summary>Customer web storefront.</summary>
    Website,

    /// <summary>Telegram bot sales channel.</summary>
    Telegram,

    /// <summary>SMM service panel.</summary>
    Smm,

    /// <summary>Admin panel manual order creation.</summary>
    Admin
}
