using System.ComponentModel.DataAnnotations;

namespace WebApiTgBot.Modules.Payments.Models.DTOs;

/// <summary>
/// Administrator payload to reject a manual payment attempt.
/// </summary>
public class RejectManualPaymentRequest
{
    /// <summary>
    /// Clear reason for rejecting the payment attempt (e.g. "Payment not found in bank statement").
    /// </summary>
    [Required(ErrorMessage = "Reason is required.")]
    [MaxLength(500, ErrorMessage = "Reason cannot exceed 500 characters.")]
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Optional additional notes for internal records.
    /// </summary>
    [MaxLength(1000, ErrorMessage = "AdminNotes cannot exceed 1000 characters.")]
    public string? AdminNotes { get; set; }
}
