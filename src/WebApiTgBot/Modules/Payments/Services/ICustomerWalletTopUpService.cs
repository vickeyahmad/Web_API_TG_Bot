using WebApiTgBot.Common.Models;
using WebApiTgBot.Modules.Payments.Models.DTOs;

namespace WebApiTgBot.Modules.Payments.Services;

/// <summary>
/// Service contract for authenticated customer manual PKR wallet top-up workflows.
/// </summary>
public interface ICustomerWalletTopUpService
{
    Task<WalletTopUpOperationResult> CreateTopUpAsync(
        string customerId,
        CreateCustomerWalletTopUpRequest request,
        string? headerIdempotencyKey,
        CancellationToken ct = default);

    Task<WalletTopUpOperationResult> SubmitProofAsync(
        string customerId,
        string paymentNumber,
        SubmitTransferProofRequest request,
        CancellationToken ct = default);

    Task<PagedResult<CustomerWalletTopUpDto>> GetCustomerTopUpsAsync(
        string customerId,
        int page = 1,
        int pageSize = 20,
        CancellationToken ct = default);

    Task<CustomerWalletTopUpDto?> GetCustomerTopUpByNumberAsync(
        string customerId,
        string paymentNumber,
        CancellationToken ct = default);
}
