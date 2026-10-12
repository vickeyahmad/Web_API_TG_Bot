namespace WebApiTgBot.Modules.Catalog.Models.DTOs.Admin;

/// <summary>
/// Query parameters for searching, filtering, and paginating vendors in the admin panel.
/// </summary>
public class AdminVendorFilterQuery
{
    /// <summary>Optional search term matching vendor internal name.</summary>
    public string? Search { get; set; }

    /// <summary>
    /// Optional active status filter:
    /// null = return both active and inactive vendors;
    /// true = active only;
    /// false = inactive only.
    /// </summary>
    public bool? IsActive { get; set; }

    /// <summary>Page number (1-based, default 1).</summary>
    public int Page { get; set; } = 1;

    /// <summary>Items per page (default 20, max 100).</summary>
    public int PageSize { get; set; } = 20;
}
