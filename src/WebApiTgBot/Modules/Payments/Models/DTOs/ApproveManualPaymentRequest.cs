using System.ComponentModel.DataAnnotations;

namespace WebApiTgBot.Modules.Payments.Models.DTOs;

/// <summary>
/// Administrator payload to confirm verified receipt of customer funds and credit wallet.
/// Requires independent verification of amount, currency, external bank reference, and receiving account.
/// </summary>
public class ApproveManualPaymentRequest
{
    /// <summary>
    /// Independently verified monetary amount actually received in receiving bank account.
    /// Must match the requested top-up amount exactly.
    /// </summary>
    [Required(ErrorMessage = "VerifiedAmount is required.")]
    [Range(0.01, 10000000.00, ErrorMessage = "VerifiedAmount must be greater than zero.")]
    public decimal VerifiedAmount { get; set; }

    /// <summary>
    /// Currency code of verified funds (must be PKR).
    /// </summary>
    [Required(ErrorMessage = "VerifiedCurrency is required.")]
    [MaxLength(10, ErrorMessage = "VerifiedCurrency cannot exceed 10 characters.")]
    public string VerifiedCurrency { get; set; } = "PKR";

    /// <summary>
    /// Verified bank transaction ID or external reference found in bank statement (e.g. 1-Link RRN, TID).
    /// Strictly checked for uniqueness within the receiving account.
    /// </summary>
    [Required(ErrorMessage = "VerifiedTransactionId is required.")]
    [MinLength(4, ErrorMessage = "VerifiedTransactionId must be at least 4 characters.")]
    [MaxLength(150, ErrorMessage = "VerifiedTransactionId cannot exceed 150 characters.")]
    public string VerifiedTransactionId { get; set; } = string.Empty;

    /// <summary>
    /// Receiving bank account, IBAN, or merchant identifier where funds were confirmed.
    /// </summary>
    [Required(ErrorMessage = "VerifiedReceivingAccount is required.")]
    [MinLength(3, ErrorMessage = "VerifiedReceivingAccount must be at least 3 characters.")]
    [MaxLength(100, ErrorMessage = "VerifiedReceivingAccount cannot exceed 100 characters.")]
    public string VerifiedReceivingAccount { get; set; } = string.Empty;

    /// <summary>
    /// Explicit confirmation by administrator that funds were independently checked and cleared in bank statement.
    /// </summary>
    [Required(ErrorMessage = "ConfirmFundsReceived is required.")]
    public bool ConfirmFundsReceived { get; set; }

    /// <summary>
    /// Internal administrator notes for audit purposes.
    /// </summary>
    [MaxLength(1000, ErrorMessage = "AdminNotes cannot exceed 1000 characters.")]
    public string? AdminNotes { get; set; }
}
