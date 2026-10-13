using WebApiTgBot.Modules.Orders.Models;

namespace WebApiTgBot.Modules.Orders.Models.DTOs;

/// <summary>
/// Query parameters for administrator order search, filtering, and server-side pagination.
/// </summary>
public class AdminOrderQuery
{
    private const int MaxPageSize = 100;
    private int _page = 1;
    private int _pageSize = 20;

    /// <summary>
    /// Current page number (1-based, default: 1).
    /// </summary>
    public int Page
    {
        get => _page;
        set => _page = value < 1 ? 1 : value;
    }

    /// <summary>
    /// Number of items per page (default: 20, max: 100).
    /// </summary>
    public int PageSize
    {
        get => _pageSize;
        set => _pageSize = value < 1 ? 20 : (value > MaxPageSize ? MaxPageSize : value);
    }

    /// <summary>
    /// Optional filter by order status (e.g. PendingPayment, Paid, Processing, Completed).
    /// </summary>
    public OrderStatus? Status { get; set; }

    /// <summary>
    /// Optional filter by sales channel (e.g. Website, TelegramBot).
    /// </summary>
    public SalesChannel? SalesChannel { get; set; }

    /// <summary>
    /// Optional search term for customer email, customer ID, or full name.
    /// </summary>
    public string? CustomerQuery { get; set; }

    /// <summary>
    /// Optional search term for order number (partial or exact).
    /// </summary>
    public string? OrderNumber { get; set; }

    /// <summary>
    /// Optional filter by product SKU snapshot.
    /// </summary>
    public string? ProductSku { get; set; }

    /// <summary>
    /// Optional start date filter (inclusive UTC).
    /// </summary>
    public DateTime? FromDate { get; set; }

    /// <summary>
    /// Optional end date filter (inclusive UTC).
    /// </summary>
    public DateTime? ToDate { get; set; }
}
