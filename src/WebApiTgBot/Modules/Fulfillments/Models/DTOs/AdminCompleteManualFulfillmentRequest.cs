using System.ComponentModel.DataAnnotations;

namespace WebApiTgBot.Modules.Fulfillments.Models.DTOs;

/// <summary>
/// Request payload for confirming manual delivery of an order line item.
/// Requires explicit confirmation and non-sensitive delivery evidence.
/// Strictly excludes credentials, passwords, vouchers, or license keys.
/// </summary>
public class AdminCompleteManualFulfillmentRequest
{
    /// <summary>
    /// Explicit confirmation by the administrator that the product was delivered
    /// through an authorized external process. Must be true.
    /// </summary>
    [Required]
    public bool ConfirmDelivered { get; set; }

    /// <summary>
    /// Safe external delivery reference or support ticket identifier (e.g. "TICKET-12345", "EXT-DELIV-987").
    /// Strictly excludes license keys, passwords, or customer credentials.
    /// </summary>
    [Required(ErrorMessage = "DeliveryReference is required.")]
    [StringLength(100, MinimumLength = 3, ErrorMessage = "DeliveryReference must be between 3 and 100 characters.")]
    public string DeliveryReference { get; set; } = string.Empty;

    /// <summary>
    /// Non-sensitive delivery channel identifier (e.g. "EmailSupport", "WhatsAppDirect", "TelegramManual").
    /// </summary>
    [Required(ErrorMessage = "DeliveryChannel is required.")]
    [StringLength(50, MinimumLength = 2, ErrorMessage = "DeliveryChannel must be between 2 and 50 characters.")]
    public string DeliveryChannel { get; set; } = string.Empty;

    /// <summary>
    /// Optional short, sanitized operational note.
    /// Strictly excludes digital delivery secrets.
    /// </summary>
    [StringLength(500, ErrorMessage = "Notes cannot exceed 500 characters.")]
    public string? Notes { get; set; }
}
