namespace WebApiTgBot.Modules.Fulfillments.Models;

/// <summary>
/// Mechanism by which a product line item is fulfilled.
/// </summary>
public enum FulfillmentMethod
{
    /// <summary>Not yet assigned to a specific delivery channel.</summary>
    Unassigned = 0,

    /// <summary>Manual delivery performed by authorized administrator or staff.</summary>
    Manual = 1,

    /// <summary>Automated delivery dispatched through an external supplier/reseller API.</summary>
    VendorApi = 2
}
