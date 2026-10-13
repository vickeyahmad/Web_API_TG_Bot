namespace WebApiTgBot.Modules.Payments.Models.DTOs;

/// <summary>
/// Detailed administrator projection of a payment attempt requiring or having received manual verification.
/// </summary>
public class AdminManualPaymentDto
{
    public long Id { get; set; }
    public string PaymentNumber { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public long? WalletId { get; set; }
    public long? OrderId { get; set; }

    public string CustomerId { get; set; } = string.Empty;
    public string? CustomerEmail { get; set; }
    public string? CustomerFullName { get; set; }

    public decimal Amount { get; set; }
    public string Currency { get; set; } = "PKR";
    public string PaymentMethodCode { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? IdempotencyKey { get; set; }
    public string? ProviderTransactionId { get; set; }

    // Customer manual proof fields
    public string? CustomerReference { get; set; }
    public DateTime? CustomerTransferDate { get; set; }
    public string? CustomerSenderAccount { get; set; }
    public string? CustomerNotes { get; set; }
    public DateTime? CustomerProofSubmittedAt { get; set; }

    // Admin manual verification fields
    public string? VerifiedByUserId { get; set; }
    public string? VerifiedByUserName { get; set; }
    public string? VerifiedReceivingAccount { get; set; }
    public string? AdminNotes { get; set; }
    public DateTime? VerifiedAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? PaidAt { get; set; }

    public IReadOnlyList<PaymentStatusHistoryDto> StatusHistory { get; set; } = Array.Empty<PaymentStatusHistoryDto>();
}
