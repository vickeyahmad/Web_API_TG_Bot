namespace WebApiTgBot.Modules.Catalog.Models;

/// <summary>
/// Maps one of OUR products to one VENDOR who can supply it.
///
/// This is the bridge table between our internal product catalog and our
/// suppliers. It holds everything a vendor needs to fulfil an order for a
/// specific product — their product ID, their cost, and their currency.
///
/// Key design rules enforced here:
/// 1. VendorCost and VendorCostCurrency are INTERNAL. Never exposed publicly.
/// 2. ExternalProductId is the vendor's ID — different from our InternalSku.
/// 3. One product can have multiple vendor mappings (different suppliers).
/// 4. Priority allows us to prefer one vendor over another.
/// 5. IsAvailable lets us disable a mapping without deleting it.
///
/// Future extensions (NOT implemented now):
/// - Vendor API call logic
/// - Vendor balance tracking
/// - Automatic vendor selection by cost / priority / availability
/// - Retry and fallback
/// - SMM-specific fields (quantity limits, service types)
/// </summary>
public class VendorProduct
{
    public int Id { get; set; }

    /// <summary>FK — which vendor this mapping belongs to.</summary>
    public int VendorId { get; set; }

    /// <summary>FK — which of our products this maps to.</summary>
    public int ProductId { get; set; }

    /// <summary>
    /// The vendor's own identifier for this product.
    /// Example: "221", "GPT45", "9811".
    /// INTERNAL ONLY — never shown to customers.
    /// Stored as string to accommodate vendors that use non-numeric IDs.
    /// </summary>
    public string ExternalProductId { get; set; } = string.Empty;

    /// <summary>
    /// The cost we pay this vendor when purchasing one unit of this product.
    /// Stored as decimal with explicit precision — never float or double.
    ///
    /// Currency is tracked separately in VendorCostCurrency because vendors
    /// charge in different currencies (PKR, USD, USDT).
    /// NEVER subtract VendorCost from SellingPrice directly if currencies differ.
    /// </summary>
    public decimal VendorCost { get; set; }

    /// <summary>
    /// ISO 4217 currency code for the vendor cost.
    /// Examples: "PKR", "USD", "USDT".
    /// USDT is kept distinct from USD to avoid confusion.
    /// INTERNAL ONLY — never shown to customers.
    /// </summary>
    public string VendorCostCurrency { get; set; } = string.Empty;

    /// <summary>
    /// Whether this vendor currently has this product in stock and can fulfil orders.
    /// Set to false to temporarily disable without deleting the mapping.
    /// </summary>
    public bool IsAvailable { get; set; } = true;

    /// <summary>
    /// Lower numbers are tried first when selecting a vendor to fulfil an order.
    /// Example: Priority 1 = preferred, Priority 99 = last resort.
    /// </summary>
    public int Priority { get; set; } = 10;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Vendor Vendor { get; set; } = null!;
    public Product Product { get; set; } = null!;
    public ICollection<WebApiTgBot.Modules.Fulfillments.Models.FulfillmentAttempt> FulfillmentAttempts { get; set; } = new List<WebApiTgBot.Modules.Fulfillments.Models.FulfillmentAttempt>();
}

