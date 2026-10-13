using WebApiTgBot.Modules.Wallets.Models;

namespace WebApiTgBot.Modules.Wallets.Services;

/// <summary>
/// Internal business service managing customer wallet balance mutations and permanent transaction ledgers.
/// All wallet credits and debits across all sales channels must execute through this service.
/// </summary>
public interface IWalletService
{
    /// <summary>
    /// Retrieves the customer's wallet for the specified currency, or initializes a new wallet with zero balance.
    /// Thread-safe and resilient against concurrent initialization races.
    /// </summary>
    Task<Wallet> GetOrCreateWalletAsync(string customerId, string currency = "PKR", CancellationToken ct = default);

    /// <summary>
    /// Retrieves the current available balance for a customer's wallet.
    /// Returns null if the customer or wallet does not exist.
    /// </summary>
    Task<decimal?> GetBalanceAsync(string customerId, string currency = "PKR", CancellationToken ct = default);

    /// <summary>
    /// Credits a customer's wallet and records an immutable ledger transaction atomically.
    /// Protected by durable idempotency key and pessimistic row locking in PostgreSQL.
    /// </summary>
    Task<WalletOperationResult> CreditAsync(WalletCreditRequest request, CancellationToken ct = default);

    /// <summary>
    /// Debits a customer's wallet and records an immutable ledger transaction atomically.
    /// Enforces non-negative balance guard, durable idempotency, and pessimistic row locking.
    /// </summary>
    Task<WalletOperationResult> DebitAsync(WalletDebitRequest request, CancellationToken ct = default);

    /// <summary>
    /// Retrieves chronological transaction ledger history for internal auditing and reconciliation.
    /// </summary>
    Task<IReadOnlyList<WalletTransaction>> GetTransactionHistoryAsync(long walletId, int page = 1, int pageSize = 50, CancellationToken ct = default);
}
