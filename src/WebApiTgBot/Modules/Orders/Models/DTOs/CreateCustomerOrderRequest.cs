using System.ComponentModel.DataAnnotations;

namespace WebApiTgBot.Modules.Orders.Models.DTOs;

/// <summary>
/// Request body for customer order placement.
/// Customer specifies only the internal product SKU.
/// Selling price, totals, currency, customer identity, and sales channel are strictly resolved server-side.
/// </summary>
public class CreateCustomerOrderRequest
{
    [Required(ErrorMessage = "Product SKU is required.")]
    [MaxLength(100, ErrorMessage = "Product SKU cannot exceed 100 characters.")]
    public string ProductSku { get; set; } = string.Empty;

    /// <summary>
    /// Optional idempotency key in request body.
    /// Can alternatively be supplied via the X-Idempotency-Key HTTP header.
    /// </summary>
    [MaxLength(100, ErrorMessage = "Idempotency key cannot exceed 100 characters.")]
    public string? IdempotencyKey { get; set; }

    /// <summary>
    /// Optional customer fulfillment notes or special instructions.
    /// </summary>
    [MaxLength(2000, ErrorMessage = "Customer notes cannot exceed 2000 characters.")]
    public string? CustomerNotes { get; set; }
}
