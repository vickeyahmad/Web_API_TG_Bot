using WebApiTgBot.Common.Models;
using WebApiTgBot.Modules.Payments.Models;
using WebApiTgBot.Modules.Payments.Models.DTOs;

namespace WebApiTgBot.Modules.Payments.Services;

/// <summary>
/// Service contract for administrator review, approval, and rejection of manual payment top-ups.
/// </summary>
public interface IAdminPaymentVerificationService
{
    Task<PagedResult<AdminManualPaymentDto>> GetManualPaymentsAsync(
        PaymentStatus? status = null,
        int page = 1,
        int pageSize = 20,
        CancellationToken ct = default);

    Task<AdminManualPaymentDto?> GetManualPaymentByNumberAsync(
        string paymentNumber,
        CancellationToken ct = default);

    Task<AdminVerificationResult> ApproveManualTopUpAsync(
        string adminUserId,
        string paymentNumber,
        ApproveManualPaymentRequest request,
        CancellationToken ct = default);

    Task<AdminVerificationResult> RejectManualTopUpAsync(
        string adminUserId,
        string paymentNumber,
        RejectManualPaymentRequest request,
        CancellationToken ct = default);
}
