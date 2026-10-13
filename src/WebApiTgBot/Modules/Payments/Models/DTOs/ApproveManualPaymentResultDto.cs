namespace WebApiTgBot.Modules.Payments.Models.DTOs;

/// <summary>
/// Result returned upon successful administrator approval and atomic wallet credit.
/// </summary>
public class ApproveManualPaymentResultDto
{
    public AdminManualPaymentDto Payment { get; set; } = null!;
    public decimal CreditedAmount { get; set; }
    public decimal WalletNewBalance { get; set; }
    public string WalletOperationKey { get; set; } = string.Empty;
}
