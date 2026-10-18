namespace WebApiTgBot.Modules.Payments.Models.DTOs;

/// <summary>
/// Safe administrative detailed projection of an individual payment attempt.
/// Supports both OrderPurchase and WalletTopUp payments with safe audit and verification metadata.
/// Prohibits credential, API key, raw vendor payload, and sensitive account leakage.
/// </summary>
public class AdminPaymentDetailDto
{
    /// <summary>Internal primary key identifier.</summary>
    public long Id { get; set; }

    /// <summary>Standardized payment number (e.g., PAY-20261013-ABC123).</summary>
    public string PaymentNumber { get; set; } = string.Empty;

    /// <summary>Payment purpose domain target: OrderPurchase or WalletTopUp.</summary>
    public string Purpose { get; set; } = string.Empty;

    /// <summary>Customer user identifier associated with the order or wallet.</summary>
    public string CustomerId { get; set; } = string.Empty;

    /// <summary>Customer full name when available.</summary>
    public string? CustomerFullName { get; set; }

    /// <summary>Customer email address when available.</summary>
    public string? CustomerEmail { get; set; }

    /// <summary>Internal order ID if linked to an order; null for wallet top-up.</summary>
    public long? OrderId { get; set; }

    /// <summary>Order number if linked to an order; null for wallet top-up.</summary>
    public string? OrderNumber { get; set; }

    /// <summary>Wallet ID if linked to a wallet; null for order purchase.</summary>
    public long? WalletId { get; set; }

    /// <summary>Monetary amount stored in explicit decimal(18,4) precision.</summary>
    public decimal Amount { get; set; }

    /// <summary>Currency code (e.g. PKR).</summary>
    public string Currency { get; set; } = "PKR";

    /// <summary>Selected payment method code (e.g. JAZZCASH, EASYPAISA, WALLET, BANK_TRANSFER).</summary>
    public string PaymentMethodCode { get; set; } = string.Empty;

    /// <summary>Current lifecycle status (Pending, Paid, Failed, Expired, Refunded, PartiallyRefunded).</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>External provider transaction ID/reference (e.g., verified bank TID) when recorded.</summary>
    public string? ProviderTransactionId { get; set; }

    /// <summary>Customer-submitted transfer reference/TID for manual payments.</summary>
    public string? CustomerReference { get; set; }

    /// <summary>Customer-reported transfer date for manual payments.</summary>
    public DateTime? CustomerTransferDate { get; set; }

    /// <summary>Customer sender account identifier, safely minimized/masked (e.g. ***1234).</summary>
    public string? CustomerSenderAccount { get; set; }

    /// <summary>Customer-submitted notes or remarks.</summary>
    public string? CustomerNotes { get; set; }

    /// <summary>UTC timestamp when manual transfer proof was submitted by customer.</summary>
    public DateTime? CustomerProofSubmittedAt { get; set; }

    /// <summary>User ID of the administrator who verified or rejected this payment.</summary>
    public string? VerifiedByUserId { get; set; }

    /// <summary>Username of the administrator who verified or rejected this payment.</summary>
    public string? VerifiedByUserName { get; set; }

    /// <summary>Receiving bank account title/number where funds were verified by administrator.</summary>
    public string? VerifiedReceivingAccount { get; set; }

    /// <summary>Administrative notes or audit remarks.</summary>
    public string? AdminNotes { get; set; }

    /// <summary>UTC timestamp when the payment was verified by an administrator.</summary>
    public DateTime? VerifiedAt { get; set; }

    /// <summary>UTC timestamp when the payment attempt was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>UTC timestamp when the payment record was last updated.</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>UTC timestamp when funds were confirmed as received and verified; null if unpaid.</summary>
    public DateTime? PaidAt { get; set; }

    /// <summary>Chronological audit history of payment status transitions.</summary>
    public IReadOnlyList<PaymentStatusHistoryDto> StatusHistory { get; set; } = Array.Empty<PaymentStatusHistoryDto>();
}
