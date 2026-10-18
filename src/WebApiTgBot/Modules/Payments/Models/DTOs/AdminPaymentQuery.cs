using WebApiTgBot.Modules.Payments.Models;

namespace WebApiTgBot.Modules.Payments.Models.DTOs;

/// <summary>
/// Query parameters for searching, filtering, and paginating general administrative payments.
/// </summary>
public class AdminPaymentQuery
{
    /// <summary>Optional filter by payment status (Pending, Paid, Failed, Expired, Refunded, PartiallyRefunded).</summary>
    public PaymentStatus? Status { get; set; }

    /// <summary>Optional filter by payment purpose (OrderPurchase, WalletTopUp).</summary>
    public PaymentPurpose? Purpose { get; set; }

    /// <summary>Optional filter by payment method code (e.g. JAZZCASH, EASYPAISA, WALLET, BANK_TRANSFER).</summary>
    public string? PaymentMethod { get; set; }

    /// <summary>Optional search term matching payment number, customer identity, email, or transaction reference.</summary>
    public string? Search { get; set; }

    /// <summary>Page number (1-based, default: 1).</summary>
    public int Page { get; set; } = 1;

    /// <summary>Page size (default: 20, max: 100).</summary>
    public int PageSize { get; set; } = 20;
}
