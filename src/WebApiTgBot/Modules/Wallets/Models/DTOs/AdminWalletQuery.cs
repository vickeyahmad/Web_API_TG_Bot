namespace WebApiTgBot.Modules.Wallets.Models.DTOs;

/// <summary>
/// Query parameters for searching, filtering, and paginating customer wallets.
/// </summary>
public class AdminWalletQuery
{
    /// <summary>Optional search term matching customer ID, email, or full name.</summary>
    public string? Search { get; set; }

    /// <summary>Optional currency code filter (e.g. PKR).</summary>
    public string? Currency { get; set; }

    /// <summary>Page number (1-based, default: 1).</summary>
    public int Page { get; set; } = 1;

    /// <summary>Page size (default: 20, max: 100).</summary>
    public int PageSize { get; set; } = 20;
}
