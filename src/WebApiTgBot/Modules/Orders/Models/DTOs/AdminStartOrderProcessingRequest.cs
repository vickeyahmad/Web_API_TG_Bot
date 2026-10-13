using System.ComponentModel.DataAnnotations;

namespace WebApiTgBot.Modules.Orders.Models.DTOs;

/// <summary>
/// Administrator request to begin processing a verified Paid order.
/// </summary>
public class AdminStartOrderProcessingRequest
{
    /// <summary>
    /// Optional context or notes describing why processing was initiated.
    /// Defaults to a standard administrative audit message if omitted.
    /// </summary>
    [MaxLength(500)]
    public string? Reason { get; set; }
}
