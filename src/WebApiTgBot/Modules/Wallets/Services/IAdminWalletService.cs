using WebApiTgBot.Common.Models;
using WebApiTgBot.Modules.Wallets.Models.DTOs;

namespace WebApiTgBot.Modules.Wallets.Services;

/// <summary>
/// Service interface for administrator read-only querying of customer wallets and immutable transaction ledgers.
/// Strictly non-mutating; does not create wallets, alter balances, or insert transactions.
/// </summary>
public interface IAdminWalletService
{
    /// <summary>
    /// Searches, filters, and paginates customer wallets.
    /// Pure read operation returning authoritative stored balances.
    /// </summary>
    Task<PagedResult<AdminWalletSummaryDto>> GetWalletsAsync(AdminWalletQuery query, CancellationToken ct = default);

    /// <summary>
    /// Retrieves safe, paginated transaction ledger history for a specific customer wallet.
    /// Returns null if the specified wallet does not exist.
    /// </summary>
    Task<PagedResult<AdminWalletTransactionDto>?> GetWalletTransactionsAsync(long walletId, AdminWalletTransactionQuery query, CancellationToken ct = default);
}
