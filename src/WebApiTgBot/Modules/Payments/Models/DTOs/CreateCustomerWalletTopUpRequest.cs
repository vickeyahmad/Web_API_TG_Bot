using System.ComponentModel.DataAnnotations;

namespace WebApiTgBot.Modules.Payments.Models.DTOs;

/// <summary>
/// Request model for initiating a manual PKR wallet top-up.
/// </summary>
public class CreateCustomerWalletTopUpRequest
{
    /// <summary>
    /// Amount in PKR to credit to the customer's wallet upon verification.
    /// </summary>
    [Range(0.01, 10000000.00, ErrorMessage = "Amount must be greater than zero.")]
    public decimal Amount { get; set; }

    /// <summary>
    /// Manual payment method code (e.g. "BANK_TRANSFER", "MANUAL_DEPOSIT", "JAZZCASH_MANUAL", "EASYPAISA_MANUAL").
    /// </summary>
    [Required(ErrorMessage = "PaymentMethodCode is required.")]
    [MaxLength(50)]
    public string PaymentMethodCode { get; set; } = string.Empty;

    /// <summary>
    /// Optional client-provided idempotency key (can also be supplied in HTTP header X-Idempotency-Key).
    /// </summary>
    [MaxLength(100)]
    public string? IdempotencyKey { get; set; }

    /// <summary>
    /// Optional customer notes or reference at initiation time.
    /// </summary>
    [MaxLength(1000)]
    public string? Notes { get; set; }
}
