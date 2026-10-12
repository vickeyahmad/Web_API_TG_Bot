namespace WebApiTgBot.Modules.Catalog.Models.DTOs.Admin;

/// <summary>
/// Query parameters for filtering and paginating vendor-to-product mappings in the admin panel.
/// </summary>
public class AdminVendorProductFilterQuery
{
    /// <summary>Optional filter by vendor ID.</summary>
    public int? VendorId { get; set; }

    /// <summary>Optional filter by internal product ID.</summary>
    public int? ProductId { get; set; }

    /// <summary>Optional filter by internal product SKU.</summary>
    public string? ProductSku { get; set; }

    /// <summary>Optional filter by mapping availability.</summary>
    public bool? IsAvailable { get; set; }

    /// <summary>Optional filter by purchase cost currency (e.g. 'PKR', 'USD', 'USDT').</summary>
    public string? Currency { get; set; }

    /// <summary>Page number (1-based, default 1).</summary>
    public int Page { get; set; } = 1;

    /// <summary>Items per page (default 20, max 100).</summary>
    public int PageSize { get; set; } = 20;
}
