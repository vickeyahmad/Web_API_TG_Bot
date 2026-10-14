using WebApiTgBot.Modules.Catalog.Models;

namespace WebApiTgBot.Modules.Orders.Models;

/// <summary>
/// A line item within an order.
/// Captures immutable snapshots of product SKU, name, and unit price at time of order creation.
///
/// Architecture note for future SMM:
/// Digital products &amp; AI tools use standard quantity * unit price formulas (e.g. 1 month voucher = 1 unit).
/// Future SMM services may charge per thousand units (rate per 1K) with custom quantity bounds.
/// The snapshot architecture accommodates this by recording the finalized unit price snapshot.
/// </summary>
public class OrderItem
{
    public long Id { get; set; }

    public long OrderId { get; set; }

    public int ProductId { get; set; }

    /// <summary>
    /// Snapshot of the product's InternalSku at order creation time.
    /// Immutable: subsequent changes to product SKU do not alter past orders.
    /// </summary>
    public string ProductSkuSnapshot { get; set; } = string.Empty;

    /// <summary>
    /// Snapshot of the product's display name at order creation time.
    /// Immutable: subsequent changes to product name do not alter past orders.
    /// </summary>
    public string ProductNameSnapshot { get; set; } = string.Empty;

    /// <summary>
    /// Number of units purchased (>= 1).
    /// </summary>
    public int Quantity { get; set; }

    /// <summary>
    /// Snapshot of the customer unit price at order creation time.
    /// Stored with decimal(18,4) precision.
    /// Immutable: price updates in the catalog never alter historical orders.
    /// </summary>
    public decimal UnitPriceSnapshot { get; set; }

    /// <summary>
    /// Currency of the line item (matches order currency, default PKR).
    /// </summary>
    public string Currency { get; set; } = "PKR";

    /// <summary>
    /// Total price for this line item (Quantity * UnitPriceSnapshot).
    /// Stored with decimal(18,4) precision.
    /// </summary>
    public decimal TotalPrice { get; set; }

    // Navigation properties
    public Order Order { get; set; } = null!;
    public Product Product { get; set; } = null!;
    public WebApiTgBot.Modules.Fulfillments.Models.Fulfillment? Fulfillment { get; set; }
}

