using System.ComponentModel.DataAnnotations;
using WebApiTgBot.Modules.Orders.Models;

namespace WebApiTgBot.Modules.Orders.Models.DTOs;

/// <summary>
/// Query parameters for listing customer orders with pagination and status filtering.
/// </summary>
public class CustomerOrderQuery
{
    private int _page = 1;
    private int _pageSize = 10;

    [Range(1, int.MaxValue, ErrorMessage = "Page must be at least 1.")]
    public int Page
    {
        get => _page;
        set => _page = value < 1 ? 1 : value;
    }

    [Range(1, 50, ErrorMessage = "Page size must be between 1 and 50.")]
    public int PageSize
    {
        get => _pageSize;
        set => _pageSize = value switch
        {
            < 1 => 10,
            > 50 => 50,
            _ => value
        };
    }

    /// <summary>
    /// Optional filter by order lifecycle status.
    /// </summary>
    public OrderStatus? Status { get; set; }
}
