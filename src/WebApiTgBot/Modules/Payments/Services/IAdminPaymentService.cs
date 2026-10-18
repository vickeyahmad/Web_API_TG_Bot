using WebApiTgBot.Common.Models;
using WebApiTgBot.Modules.Payments.Models.DTOs;

namespace WebApiTgBot.Modules.Payments.Services;

/// <summary>
/// Service interface for administrator read-only querying of central payments across all purposes.
/// Strictly non-mutating; does not alter payment records, orders, or wallet balances.
/// </summary>
public interface IAdminPaymentService
{
    /// <summary>
    /// Searches, filters, and paginates payments across all channels and purposes (OrderPurchase and WalletTopUp).
    /// </summary>
    Task<PagedResult<AdminPaymentSummaryDto>> GetPaymentsAsync(AdminPaymentQuery query, CancellationToken ct = default);

    /// <summary>
    /// Retrieves comprehensive payment details including order/wallet links and status audit history.
    /// Returns null if the payment number does not exist.
    /// </summary>
    Task<AdminPaymentDetailDto?> GetPaymentByNumberAsync(string paymentNumber, CancellationToken ct = default);
}
