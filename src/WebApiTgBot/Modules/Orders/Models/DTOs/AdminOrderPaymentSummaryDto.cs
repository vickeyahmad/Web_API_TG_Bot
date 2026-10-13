namespace WebApiTgBot.Modules.Orders.Models.DTOs;

/// <summary>
/// Safe payment summary for administrator order listings.
/// Excludes sensitive banking details and distinguishes payment lifecycle states.
/// </summary>
public class AdminOrderPaymentSummaryDto
{
    public string? PaymentNumber { get; set; }
    public string? PaymentMethod { get; set; }
    public string? PaymentStatus { get; set; }
    public decimal? AmountPaid { get; set; }
    public string? Currency { get; set; }
    public DateTime? PaidAt { get; set; }
}
