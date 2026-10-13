using WebApiTgBot.Modules.Payments.Models.DTOs;

namespace WebApiTgBot.Modules.Payments.Services;

/// <summary>
/// Service coordinating the atomic payment of customer orders using available PKR wallet balances.
/// Enforces single-transaction execution across Orders, Payments, and Wallets with pessimistic row locking.
/// </summary>
public interface IWalletOrderPaymentService
{
    /// <summary>
    /// Executes the payment of a customer's order using available PKR wallet balance.
    /// Runs atomically in a single PostgreSQL transaction with row-level locks on Order and Wallet.
    /// </summary>
    Task<WalletOrderPaymentResult> PayOrderWithWalletAsync(
        string customerId,
        string orderNumber,
        string? idempotencyKey,
        CancellationToken ct = default);
}
