namespace WebApiTgBot.Modules.Payments.Models.DTOs;

/// <summary>
/// Data transfer object representing a customer's wallet top-up payment attempt.
/// </summary>
public class CustomerWalletTopUpDto
{
    public string PaymentNumber { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "PKR";
    public string PaymentMethodCode { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? PaidAt { get; set; }
    public DateTime? CustomerProofSubmittedAt { get; set; }
    public string? CustomerReference { get; set; }
    public DateTime? CustomerTransferDate { get; set; }
    public string? CustomerSenderAccount { get; set; }
    public string? CustomerNotes { get; set; }

    // Receiving instructions for manual bank/deposit
    public string? BankName { get; set; }
    public string? AccountTitle { get; set; }
    public string? AccountNumber { get; set; }
    public string? Instructions { get; set; }
}
