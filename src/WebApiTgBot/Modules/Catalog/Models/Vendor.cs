namespace WebApiTgBot.Modules.Catalog.Models;

/// <summary>
/// A vendor (supplier) who can fulfil one or more of our products.
///
/// Vendors are completely internal. Customers never see vendor names, IDs,
/// costs, API credentials, or any other vendor information.
///
/// A vendor can represent:
/// - An API-based reseller website
/// - An SMM panel
/// - An official reseller API
/// - A manually fulfilled supplier
/// - (Future) A Telegram bot supplier — only after reliability/authorization review
///
/// Vendor API integration, payment to vendors, and vendor selection logic
/// are NOT implemented in Milestone 2A. This table is the foundation.
/// </summary>
public class Vendor
{
    public int Id { get; set; }

    /// <summary>
    /// Our internal name for this vendor.
    /// Used only by admins and internal systems.
    /// NEVER exposed to customers.
    /// </summary>
    public string InternalName { get; set; } = string.Empty;

    /// <summary>
    /// Whether this vendor is currently available for use.
    /// Inactive vendors are skipped during order fulfilment.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation — one vendor can supply many products
    public ICollection<VendorProduct> VendorProducts { get; set; } = new List<VendorProduct>();
}
