namespace WebApiTgBot.Modules.Payments.Models.DTOs;

/// <summary>
/// Safe administrative projection of a payment attempt for summary listing.
/// Supports both OrderPurchase and WalletTopUp payments without exposing internal secrets or vendor data.
/// </summary>
public class AdminPaymentSummaryDto
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

    /// <summary>Order number if purpose is OrderPurchase; null for WalletTopUp.</summary>
    public string? OrderNumber { get; set; }

    /// <summary>Wallet ID if purpose is WalletTopUp; null for OrderPurchase.</summary>
    public long? WalletId { get; set; }

    /// <summary>Monetary amount stored in explicit decimal(18,4) precision.</summary>
    public decimal Amount { get; set; }

    /// <summary>Currency code (e.g. PKR).</summary>
    public string Currency { get; set; } = "PKR";

    /// <summary>Selected payment method code (e.g. JAZZCASH, EASYPAISA, WALLET, BANK_TRANSFER).</summary>
    public string PaymentMethodCode { get; set; } = string.Empty;

    /// <summary>Current lifecycle status (Pending, Paid, Failed, Expired, Refunded, PartiallyRefunded).</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>UTC timestamp when the payment attempt was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>UTC timestamp when the payment record was last updated.</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>UTC timestamp when funds were confirmed as received and verified; null if unpaid.</summary>
    public DateTime? PaidAt { get; set; }
}
