using System.ComponentModel.DataAnnotations;

namespace WebApiTgBot.Modules.Payments.Models.DTOs;

/// <summary>
/// Request payload when customer submits manual bank transfer proof details.
/// Wallet is NEVER credited at this stage; status remains Pending awaiting administrator verification.
/// </summary>
public class SubmitTransferProofRequest
{
    /// <summary>
    /// Bank transfer transaction ID, TID, or reference number provided by customer.
    /// </summary>
    [Required(ErrorMessage = "CustomerReference is required.")]
    [MaxLength(150, ErrorMessage = "CustomerReference cannot exceed 150 characters.")]
    public string CustomerReference { get; set; } = string.Empty;

    /// <summary>
    /// Optional date and time when customer made the transfer.
    /// </summary>
    public DateTime? CustomerTransferDate { get; set; }

    /// <summary>
    /// Optional sender bank account name, number, or mobile number.
    /// </summary>
    [MaxLength(100, ErrorMessage = "CustomerSenderAccount cannot exceed 100 characters.")]
    public string? CustomerSenderAccount { get; set; }

    /// <summary>
    /// Optional customer notes regarding the transfer.
    /// </summary>
    [MaxLength(1000, ErrorMessage = "CustomerNotes cannot exceed 1000 characters.")]
    public string? CustomerNotes { get; set; }
}
