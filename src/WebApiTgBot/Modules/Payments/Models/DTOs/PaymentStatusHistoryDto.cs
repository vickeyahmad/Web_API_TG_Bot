namespace WebApiTgBot.Modules.Payments.Models.DTOs;

/// <summary>
/// Audit trail entry for payment status lifecycle changes.
/// </summary>
public class PaymentStatusHistoryDto
{
    public long Id { get; set; }
    public string? PreviousStatus { get; set; }
    public string NewStatus { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string? ChangedByUserId { get; set; }
    public string? ChangedByUserName { get; set; }
    public DateTime ChangedAt { get; set; }
}
