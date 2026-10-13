namespace WebApiTgBot.Modules.Payments.Models.DTOs;

/// <summary>
/// Safe customer-facing response returned after successfully paying an order with wallet balance.
/// Excludes vendor, provider, and internal administrative details.
/// </summary>
public class CustomerOrderPaymentDto
{
    public string PaymentNumber { get; set; } = string.Empty;
    public string OrderNumber { get; set; } = string.Empty;
    public string OrderStatus { get; set; } = string.Empty;
    public string PaymentStatus { get; set; } = string.Empty;
    public decimal AmountPaid { get; set; }
    public string Currency { get; set; } = "PKR";
    public string PaymentMethod { get; set; } = "WALLET";
    public decimal WalletRemainingBalance { get; set; }
    public DateTime PaidAt { get; set; }
}
