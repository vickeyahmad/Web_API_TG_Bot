namespace WebApiTgBot.Modules.Catalog.Models.DTOs.Admin;

/// <summary>
/// Query parameters for filtering and paginating products in the admin panel.
/// </summary>
public class AdminProductFilterQuery
{
    /// <summary>Optional category ID filter.</summary>
    public int? CategoryId { get; set; }

    /// <summary>Optional category slug filter.</summary>
    public string? CategorySlug { get; set; }

    /// <summary>Optional search term matching product name, internal SKU, or description.</summary>
    public string? Search { get; set; }

    /// <summary>
    /// Optional active status filter:
    /// null = return both active and inactive products;
    /// true = active only;
    /// false = inactive only.
    /// </summary>
    public bool? IsActive { get; set; }

    /// <summary>Page number (1-based, default 1).</summary>
    public int Page { get; set; } = 1;

    /// <summary>Items per page (default 20, max 100).</summary>
    public int PageSize { get; set; } = 20;
}
