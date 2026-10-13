namespace WebApiTgBot.Modules.Orders.Models.DTOs;

/// <summary>
/// Administrator representation of an OrderPurchase payment attempt.
/// Safely presents payment status, timestamps, and wallet references without leaking sensitive credentials.
/// </summary>
public class AdminOrderPaymentDto
{
    public string PaymentNumber { get; set; } = string.Empty;
    public string PaymentMethod { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "PKR";
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? PaidAt { get; set; }
    public DateTime? ExpiredAt { get; set; }
    public string? ProviderTransactionId { get; set; }
}
