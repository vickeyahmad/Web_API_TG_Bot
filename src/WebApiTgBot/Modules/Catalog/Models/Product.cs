namespace WebApiTgBot.Modules.Catalog.Models;

/// <summary>
/// A product that we sell to customers.
///
/// Products are generic by design. AI Tools, Digital Products, and future SMM services
/// all live in this same table, distinguished only by their Category.
///
/// Specialised attributes (e.g. SMM quantity limits) will be added to separate
/// extension tables in later milestones — this table stays clean.
/// </summary>
public class Product
{
    public int Id { get; set; }

    /// <summary>
    /// Our unique internal identifier for this product.
    /// Example: "CHATGPT_PLUS_1M"
    ///
    /// This is what customers and APIs use to refer to the product.
    /// It is NEVER the vendor's external ID.
    /// Must be unique, uppercase, alphanumeric + underscores.
    /// </summary>
    public string InternalSku { get; set; } = string.Empty;

    /// <summary>Customer-facing display name. Example: "ChatGPT Plus — 1 Month".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional customer-facing description.</summary>
    public string? Description { get; set; }

    /// <summary>FK — which category this product belongs to.</summary>
    public int CategoryId { get; set; }

    /// <summary>
    /// The price we charge the customer.
    /// Stored as decimal with explicit precision (18,4) — never float or double.
    /// </summary>
    public decimal SellingPrice { get; set; }

    /// <summary>
    /// ISO 4217 currency code for the selling price.
    /// Default is PKR (Pakistani Rupee) — our primary market.
    /// Kept separate from vendor cost currency because they are often different.
    /// Example: customer pays 5,000 PKR; vendor charges 12 USDT.
    /// </summary>
    public string SellingCurrency { get; set; } = "PKR";

    /// <summary>Only active products in active categories are shown publicly.</summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Category Category { get; set; } = null!;

    /// <summary>
    /// All vendor mappings for this product.
    /// INTERNAL ONLY — never serialised in public API responses.
    /// </summary>
    public ICollection<VendorProduct> VendorProducts { get; set; } = new List<VendorProduct>();
}
